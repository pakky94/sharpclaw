using Dapper;
using Npgsql;

namespace SharpClaw.API.Agents;

public class Tooling
{
    public static async Task<string[]> ListFiles(IServiceProvider serviceProvider)
    {
        Console.WriteLine($"Listing files");
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        await using var connection = new NpgsqlConnection(ctx.DbConnectionString);

        var files = (await connection.QueryAsync<string>(
            """
            select d.name from agents a
            join agents_documents ad on a.id = ad.agent_id
            join documents d on d.id = ad.document_id
            where a.Id = @agentId
            """,
            new
            {
                ctx.AgentId,
            })).ToArray();

        Console.WriteLine($"Files found: {string.Join(", ", files)}");

        return files;
    }

    public static async Task<string> ReadFile(IServiceProvider serviceProvider, string path)
    {
        Console.WriteLine($"Reading file: {path}");
        var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
        await using var connection = new NpgsqlConnection(ctx.DbConnectionString);

        var content = await connection.QueryFirstAsync<string>(
            """
            select d.content from agents a
            join agents_documents ad on a.id = ad.agent_id
            join documents d on d.id = ad.document_id
            where a.Id = @agentId and d.name = @path;
            """,
            new
            {
                ctx.AgentId,
                path,
            });

        Console.WriteLine($"Read content: {content}");

        return content;
    }

    public static async Task WriteFile(IServiceProvider serviceProvider, string path, string content)
    {
        try
        {
            Console.WriteLine($"Writing file: {path} - {content}");
            var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
            await using var connection = new NpgsqlConnection(ctx.DbConnectionString);

            await connection.ExecuteAsync(
                """
                update documents as d
                set content=@content
                from agents_documents ad
                         join agents a on a.id = ad.agent_id
                where d.Id = ad.document_id and d.name=@path and a.id = @agentId;

                with documents_id as (
                    insert into documents (name, content)
                        select @path, @content
                        where not exists (
                            select 1
                            from documents d
                                     join agents_documents ad on ad.document_id = d.id
                                     join agents a on a.id = ad.agent_id
                            where d.name=@path and a.id = @agentId)
                        returning id
                )
                insert into agents_documents (agent_id, document_id)
                select @agentId, documents_id.id
                from documents_id;
                """,
                new
                {
                    ctx.AgentId,
                    path,
                    content,
                });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error writing file: {path} - {content} - {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            throw;
        }
    }

    public static async Task DeleteFile(IServiceProvider serviceProvider, string path)
    {
        // TODO: add softdelete
        try
        {
            Console.WriteLine($"Deleting file: {path}");
            var ctx = serviceProvider.GetRequiredService<AgentExecutionContext>();
            await using var connection = new NpgsqlConnection(ctx.DbConnectionString);

            await connection.ExecuteAsync(
                """
                delete from agents_documents
                where id in (select ad.id
                                      from documents d
                                               join agents_documents ad on ad.document_id = d.id
                                               join agents a on a.id = ad.agent_id
                                      where d.name=@path and a.id = @agentId);

                delete from documents
                where id in (select d.id
                             from documents d
                                      join agents_documents ad on ad.document_id = d.id
                                      join agents a on a.id = ad.agent_id
                             where d.name=@path and a.id = @agentId);
                """,
                new
                {
                    ctx.AgentId,
                    path,
                });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deleting file: {path} - {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            throw;
        }
    }
}