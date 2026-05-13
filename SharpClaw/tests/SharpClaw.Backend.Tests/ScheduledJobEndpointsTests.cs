using System.Text.Json;
using SharpClaw.Backend.Tests.Infrastructure;

namespace SharpClaw.Backend.Tests;

[Collection(SharpClawAppFixture.CollectionName)]
public sealed class ScheduledJobEndpointsTests(SharpClawAppFixture fixture)
{
    [Fact]
    public async Task ListJobs_ReturnsEmpty_WhenNoJobsExist()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.ListScheduledJobsAsync();
        var jobs = result.RootElement.GetProperty("jobs").EnumerateArray().ToArray();

        Assert.Empty(jobs);
    }

    [Fact]
    public async Task CreateJob_WithValidData_ReturnsCreatedJob()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.CreateScheduledJobAsync(
            name: "Test Job",
            cronExpression: "0 8 * * *",
            prompt: "Summarize today's schedule",
            agentId: 1,
            timezone: "Europe/Rome");

        Assert.Equal("Test Job", result.RootElement.GetProperty("name").GetString());
        Assert.Equal("0 8 * * *", result.RootElement.GetProperty("cronExpression").GetString());
        Assert.Equal("Europe/Rome", result.RootElement.GetProperty("timezone").GetString());
        Assert.Equal("Summarize today's schedule", result.RootElement.GetProperty("prompt").GetString());
        Assert.Equal(1, result.RootElement.GetProperty("agentId").GetInt64());
        Assert.True(result.RootElement.GetProperty("enabled").GetBoolean());
        Assert.True(result.RootElement.GetProperty("id").GetInt64() > 0);

        // nextRunAt should be in the future
        var nextRunAt = result.RootElement.GetProperty("nextRunAt").GetDateTimeOffset();
        Assert.True(nextRunAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task CreateJob_WithInvalidCron_Returns400()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.CreateScheduledJobAsync(
                name: "Bad Job",
                cronExpression: "not-a-cron",
                prompt: "test",
                agentId: 1));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task CreateJob_WithInvalidTimezone_Returns400()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.CreateScheduledJobAsync(
                name: "Bad TZ",
                cronExpression: "0 8 * * *",
                prompt: "test",
                agentId: 1,
                timezone: "Mars/Olympus"));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task CreateJob_WithNonexistentAgent_Returns400()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.CreateScheduledJobAsync(
                name: "Bad Agent",
                cronExpression: "0 8 * * *",
                prompt: "test",
                agentId: 9999));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task CreateJob_WithMissingName_Returns400()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.CreateScheduledJobAsync(
                name: "",
                cronExpression: "0 8 * * *",
                prompt: "test",
                agentId: 1));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task CreateJob_WithMissingPrompt_Returns400()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.CreateScheduledJobAsync(
                name: "No Prompt",
                cronExpression: "0 8 * * *",
                prompt: "",
                agentId: 1));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task CreateJob_WithDisabledFlag_ReturnsDisabledJob()
    {
        await fixture.ResetStateAsync();

        using var result = await fixture.Api.CreateScheduledJobAsync(
            name: "Disabled Job",
            cronExpression: "0 8 * * *",
            prompt: "test",
            agentId: 1,
            enabled: false);

        Assert.False(result.RootElement.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task ListJobs_ReturnsCreatedJobs()
    {
        await fixture.ResetStateAsync();

        await fixture.Api.CreateScheduledJobAsync(
            name: "Job A", cronExpression: "0 8 * * *", prompt: "A", agentId: 1);
        await fixture.Api.CreateScheduledJobAsync(
            name: "Job B", cronExpression: "30 9 * * 1-5", prompt: "B", agentId: 1);

        using var result = await fixture.Api.ListScheduledJobsAsync();
        var jobs = result.RootElement.GetProperty("jobs").EnumerateArray().ToArray();

        Assert.Equal(2, jobs.Length);
        Assert.Contains(jobs, j => j.GetProperty("name").GetString() == "Job A");
        Assert.Contains(jobs, j => j.GetProperty("name").GetString() == "Job B");
    }

    [Fact]
    public async Task UpdateJob_ChangesFields()
    {
        await fixture.ResetStateAsync();

        using var created = await fixture.Api.CreateScheduledJobAsync(
            name: "Original", cronExpression: "0 8 * * *", prompt: "original", agentId: 1);

        var id = created.RootElement.GetProperty("id").GetInt64();

        using var updated = await fixture.Api.UpdateScheduledJobAsync(
            id,
            name: "Updated",
            prompt: "updated prompt",
            enabled: false);

        Assert.Equal("Updated", updated.RootElement.GetProperty("name").GetString());
        Assert.Equal("updated prompt", updated.RootElement.GetProperty("prompt").GetString());
        Assert.False(updated.RootElement.GetProperty("enabled").GetBoolean());
        // Cron should be unchanged
        Assert.Equal("0 8 * * *", updated.RootElement.GetProperty("cronExpression").GetString());
    }

    [Fact]
    public async Task UpdateJob_ChangingCron_RecomputesNextRunAt()
    {
        await fixture.ResetStateAsync();

        using var created = await fixture.Api.CreateScheduledJobAsync(
            name: "Cron Change", cronExpression: "0 8 * * *", prompt: "test", agentId: 1);

        var id = created.RootElement.GetProperty("id").GetInt64();
        var originalNextRun = created.RootElement.GetProperty("nextRunAt").GetDateTimeOffset();

        using var updated = await fixture.Api.UpdateScheduledJobAsync(
            id, cronExpression: "0 20 * * *");

        var newNextRun = updated.RootElement.GetProperty("nextRunAt").GetDateTimeOffset();
        Assert.NotEqual(originalNextRun, newNextRun);
    }

    [Fact]
    public async Task UpdateJob_WithInvalidCron_Returns400()
    {
        await fixture.ResetStateAsync();

        using var created = await fixture.Api.CreateScheduledJobAsync(
            name: "Valid", cronExpression: "0 8 * * *", prompt: "test", agentId: 1);

        var id = created.RootElement.GetProperty("id").GetInt64();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.UpdateScheduledJobAsync(id, cronExpression: "bad-cron"));

        Assert.Contains("400", exception.Message);
    }

    [Fact]
    public async Task UpdateJob_NotFound_Returns404()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.UpdateScheduledJobAsync(9999, name: "Ghost"));

        Assert.Contains("404", exception.Message);
    }

    [Fact]
    public async Task DeleteJob_RemovesJob()
    {
        await fixture.ResetStateAsync();

        using var created = await fixture.Api.CreateScheduledJobAsync(
            name: "To Delete", cronExpression: "0 8 * * *", prompt: "test", agentId: 1);

        var id = created.RootElement.GetProperty("id").GetInt64();

        using var deleteResult = await fixture.Api.DeleteScheduledJobAsync(id);
        Assert.True(deleteResult.RootElement.GetProperty("deleted").GetBoolean());

        // Verify it's gone
        using var list = await fixture.Api.ListScheduledJobsAsync();
        var jobs = list.RootElement.GetProperty("jobs").EnumerateArray().ToArray();
        Assert.DoesNotContain(jobs, j => j.GetProperty("id").GetInt64() == id);
    }

    [Fact]
    public async Task DeleteJob_NotFound_Returns404()
    {
        await fixture.ResetStateAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            fixture.Api.DeleteScheduledJobAsync(9999));

        Assert.Contains("404", exception.Message);
    }
}
