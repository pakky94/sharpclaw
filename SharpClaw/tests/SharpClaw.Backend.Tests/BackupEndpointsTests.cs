using SharpClaw.Backend.Tests.Infrastructure;

namespace SharpClaw.Backend.Tests;

[Collection(SharpClawAppFixture.CollectionName)]
public sealed class BackupEndpointsTests(SharpClawAppFixture fixture)
{
    [Fact]
    public async Task BackupConfig_CanBeUpdated_AndReadBack()
    {
        await fixture.ResetStateAsync();
        var backupDir = CreateTempBackupDirectory();

        try
        {
            using var updated = await fixture.Api.UpdateBackupConfigAsync(
                timezone: "UTC",
                dailyTime: new TimeOnly(4, 30, 0),
                fullEveryN: 3,
                strictRestoreDefault: false,
                storageRoot: backupDir);

            Assert.Equal("UTC", updated.RootElement.GetProperty("timezone").GetString());
            Assert.Equal(3, updated.RootElement.GetProperty("fullEveryN").GetInt32());
            Assert.False(updated.RootElement.GetProperty("strictRestoreDefault").GetBoolean());
            Assert.Equal(backupDir, updated.RootElement.GetProperty("storageRoot").GetString());

            using var fetched = await fixture.Api.GetBackupConfigAsync();
            Assert.Equal("UTC", fetched.RootElement.GetProperty("timezone").GetString());
            Assert.Equal("04:30:00", fetched.RootElement.GetProperty("dailyTime").GetString());
            Assert.Equal(3, fetched.RootElement.GetProperty("fullEveryN").GetInt32());
            Assert.Equal(backupDir, fetched.RootElement.GetProperty("storageRoot").GetString());
        }
        finally
        {
            SafeDeleteDirectory(backupDir);
        }
    }

    [Fact]
    public async Task RunBackup_Full_CreatesSucceededRunAndArtifact()
    {
        await fixture.ResetStateAsync();
        var backupDir = CreateTempBackupDirectory();

        try
        {
            await fixture.Api.UpdateBackupConfigAsync(
                timezone: "UTC",
                storageRoot: backupDir);

            using var run = await fixture.Api.RunBackupAsync("full");

            Assert.Equal("full", run.RootElement.GetProperty("backupType").GetString());
            Assert.Equal("succeeded", run.RootElement.GetProperty("status").GetString());

            var artifactPath = run.RootElement.GetProperty("artifactPath").GetString();
            Assert.False(string.IsNullOrWhiteSpace(artifactPath));
            Assert.True(File.Exists(artifactPath), $"Expected backup artifact at '{artifactPath}'.");

            using var listed = await fixture.Api.ListBackupRunsAsync();
            var runs = listed.RootElement.GetProperty("runs").EnumerateArray().ToArray();
            Assert.NotEmpty(runs);
            Assert.Contains(runs, r => r.GetProperty("backupType").GetString() == "full");
        }
        finally
        {
            SafeDeleteDirectory(backupDir);
        }
    }

    [Fact]
    public async Task RestoreBackup_IncrementalChain_RestoresUpdatedAndDeletedData()
    {
        await fixture.ResetStateAsync();
        var backupDir = CreateTempBackupDirectory();

        try
        {
            await fixture.Api.UpdateBackupConfigAsync(
                timezone: "UTC",
                storageRoot: backupDir,
                fullEveryN: 7,
                strictRestoreDefault: true);

            var sessionId = await fixture.Api.CreateSessionAsync(agentId: 1, name: "name-v1");
            using var oldChannel = await fixture.Api.CreateChannelAsync(
                name: "channel-old",
                type: "discord",
                agentId: 1);
            var oldChannelId = oldChannel.RootElement.GetProperty("id").GetInt64();

            using var fullRun = await fixture.Api.RunBackupAsync("full");
            Assert.Equal("succeeded", fullRun.RootElement.GetProperty("status").GetString());

            await fixture.Api.RenameSessionAsync(sessionId, "name-v2");
            await fixture.Api.DeleteChannelAsync(oldChannelId);

            using var incrementalRun = await fixture.Api.RunBackupAsync("incremental");
            Assert.Equal("incremental", incrementalRun.RootElement.GetProperty("backupType").GetString());
            Assert.Equal("succeeded", incrementalRun.RootElement.GetProperty("status").GetString());
            var restoreBackupId = incrementalRun.RootElement.GetProperty("backupId").GetGuid();

            await fixture.Api.RenameSessionAsync(sessionId, "name-v3");

            await fixture.Api.RestoreBackupAsync(restoreBackupId, strict: true);

            using var sessions = await fixture.Api.GetSessionsAsync(agentId: 1);
            var session = sessions.RootElement.GetProperty("sessions")
                .EnumerateArray()
                .Single(s => s.GetProperty("sessionId").GetGuid() == sessionId);
            Assert.Equal("name-v2", session.GetProperty("name").GetString());

            using var channels = await fixture.Api.ListChannelsAsync();
            var listedChannels = channels.RootElement.GetProperty("channels").EnumerateArray().ToArray();
            Assert.DoesNotContain(listedChannels, c => c.GetProperty("id").GetInt64() == oldChannelId);
        }
        finally
        {
            SafeDeleteDirectory(backupDir);
        }
    }

    private static string CreateTempBackupDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "sharpclaw-backups-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void SafeDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore cleanup failures in tests.
        }
    }
}
