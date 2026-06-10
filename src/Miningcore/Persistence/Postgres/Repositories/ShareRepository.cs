using System.Data;
using AutoMapper;
using Dapper;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Model.Projections;
using Miningcore.Persistence.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Miningcore.Persistence.Postgres.Repositories;

public class ShareRepository : IShareRepository
{
    public ShareRepository(IMapper mapper)
    {
        this.mapper = mapper;
    }

    private readonly IMapper mapper;

    public async Task BatchInsertAsync(IDbConnection con, IDbTransaction tx, IEnumerable<Share> shares, CancellationToken ct)
    {
        // NOTE: Even though the tx parameter is completely ignored here,
        // the COPY command still honors a current ambient transaction

        var pgCon = (NpgsqlConnection)con;

        const string query = @"COPY shares (poolid, blockheight, difficulty,
            networkdifficulty, miner, worker, useragent, ipaddress, source, created) FROM STDIN (FORMAT BINARY)";

        await using (var writer = await pgCon.BeginBinaryImportAsync(query, ct))
        {
            foreach (var share in shares)
            {
                await writer.StartRowAsync(ct);

                await writer.WriteAsync(share.PoolId, ct);
                await writer.WriteAsync((long)share.BlockHeight, NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(share.Difficulty, NpgsqlDbType.Double, ct);
                await writer.WriteAsync(share.NetworkDifficulty, NpgsqlDbType.Double, ct);
                await writer.WriteAsync(share.Miner, ct);
                await writer.WriteAsync(share.Worker, ct);
                await writer.WriteAsync(share.UserAgent, ct);
                await writer.WriteAsync(share.IpAddress, ct);
                await writer.WriteAsync(share.Source, ct);
                await writer.WriteAsync(share.Created, NpgsqlDbType.TimestampTz, ct);
            }

            await writer.CompleteAsync(ct);
        }
    }

    public async Task<Share[]> ReadSharesBeforeAsync(IDbConnection con, string poolId, DateTime before,
        bool inclusive, int pageSize, CancellationToken ct)
    {
        // Select only the columns needed for payout calculations to reduce data transfer
        var query = @$"SELECT poolid, blockheight, miner, difficulty, networkdifficulty, created FROM shares
            WHERE poolid = @poolId AND created {(inclusive ? " <= " : " < ")} @before
            ORDER BY created DESC FETCH NEXT @pageSize ROWS ONLY";

        var entities = await con.QueryAsync<Entities.Share>(new CommandDefinition(query,
            new { poolId, before, pageSize }, null, null, null, CommandFlags.None, ct));

        return entities
            .Select(mapper.Map<Share>)
            .ToArray();
    }

    public async Task<Share[]> ReadSharesBeforeAsync(IDbConnection con, string poolId, DateTime before,
        bool inclusive, int pageSize, int pageOffset, CancellationToken ct)
    {
        // Select only the columns needed for payout calculations to reduce data transfer
        var query = @$"SELECT poolid, blockheight, miner, difficulty, networkdifficulty, created FROM shares
            WHERE poolid = @poolId AND created {(inclusive ? " <= " : " < ")} @before
            ORDER BY created DESC OFFSET @pageOffset FETCH NEXT @pageSize ROWS ONLY";

        var entities = await con.QueryAsync<Entities.Share>(new CommandDefinition(query,
            new { poolId, before, pageSize, pageOffset }, null, null, null, CommandFlags.None, ct));

        return entities
            .Select(mapper.Map<Share>)
            .ToArray();
    }

    public IEnumerable<Share> StreamSharesBefore(IDbConnection con, string poolId, DateTime before, bool inclusive)
    {
        return StreamSharesBefore(con, poolId, before, inclusive, ascending: false);
    }

    public IEnumerable<Share> StreamSharesBefore(IDbConnection con, string poolId, DateTime before, bool inclusive, bool ascending)
    {
        const string query = @"SELECT poolid, blockheight, miner, difficulty, networkdifficulty, created FROM shares
            WHERE poolid = @poolId AND created {0} @before
            ORDER BY created {1}";

        var sql = string.Format(query, inclusive ? "<=" : "<", ascending ? "ASC" : "DESC");

        var entities = con.Query<Entities.Share>(sql, new { poolId, before }, buffered: false);

        foreach (var entity in entities)
            yield return mapper.Map<Share>(entity);
    }

    public IEnumerable<Share> StreamSharesBetween(IDbConnection con, string poolId, DateTime after, DateTime before, bool beforeInclusive, bool ascending)
    {
        const string query = @"SELECT poolid, blockheight, miner, difficulty, networkdifficulty, created FROM shares
            WHERE poolid = @poolId AND created > @after AND created {0} @before
            ORDER BY created {1}";

        var sql = string.Format(query, beforeInclusive ? "<=" : "<", ascending ? "ASC" : "DESC");

        var entities = con.Query<Entities.Share>(sql, new { poolId, after, before }, buffered: false);

        foreach (var entity in entities)
            yield return mapper.Map<Share>(entity);
    }

    public Task<long> CountSharesBeforeAsync(IDbConnection con, IDbTransaction tx, string poolId, DateTime before, CancellationToken ct)
    {
        const string query = "SELECT count(*) FROM shares WHERE poolid = @poolId AND created < @before";

        return con.QuerySingleAsync<long>(new CommandDefinition(query, new { poolId, before }, tx, cancellationToken: ct));
    }

    public Task<long> CountSharesByMinerAsync(IDbConnection con, IDbTransaction tx, string poolId, string miner, CancellationToken ct)
    {
        const string query = "SELECT count(*) FROM shares WHERE poolid = @poolId AND miner = @miner";

        return con.QuerySingleAsync<long>(new CommandDefinition(query, new { poolId, miner }, tx, cancellationToken: ct));
    }

    public Task<double?> GetEffortBetweenCreatedAsync(IDbConnection con, string poolId, double shareConst, DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = "SELECT SUM((difficulty * @shareConst) / networkdifficulty) FROM shares WHERE poolid = @poolId AND created > @start AND created < @end";

        return con.QuerySingleAsync<double?>(new CommandDefinition(query, new { poolId, shareConst, start, end }, cancellationToken: ct));
    }

    public Task<double?> GetMinerEffortBetweenCreatedAsync(IDbConnection con, string poolId, string miner, DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = "SELECT SUM(difficulty / networkdifficulty) FROM shares WHERE poolid = @poolId AND miner = @miner AND created > @start AND created < @end";

        return con.QuerySingleAsync<double?>(new CommandDefinition(query, new { poolId, miner, start, end }, cancellationToken: ct));
    }

    public async Task DeleteSharesByMinerAsync(IDbConnection con, IDbTransaction tx, string poolId, string miner, CancellationToken ct)
    {
        const string query = "DELETE FROM shares WHERE poolid = @poolId AND miner = @miner";

        await con.ExecuteAsync(new CommandDefinition(query, new { poolId, miner }, tx, cancellationToken: ct));
    }

    public async Task DeleteSharesBeforeAsync(IDbConnection con, IDbTransaction tx, string poolId, DateTime before, CancellationToken ct)
    {
        const string query = "DELETE FROM shares WHERE poolid = @poolId AND created < @before";

        await con.ExecuteAsync(new CommandDefinition(query, new { poolId, before }, tx, cancellationToken: ct));
    }

    public Task<int> DeleteSharesBeforeAsync(IDbConnection con, IDbTransaction tx, string poolId, DateTime before, int pageSize, CancellationToken ct)
    {
        const string query = @"WITH candidates AS (
                SELECT ctid FROM shares
                WHERE poolid = @poolId AND created < @before
                ORDER BY created
                LIMIT @pageSize
            ), deleted AS (
                DELETE FROM shares s
                USING candidates c
                WHERE s.ctid = c.ctid
                RETURNING 1
            )
            SELECT COUNT(*) FROM deleted";

        return con.ExecuteScalarAsync<int>(new CommandDefinition(query,
            new { poolId, before, pageSize }, tx, cancellationToken: ct));
    }

    public Task<double?> GetAccumulatedShareDifficultyBetweenAsync(IDbConnection con, string poolId, DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = "SELECT SUM(difficulty) FROM shares WHERE poolid = @poolId AND created > @start AND created < @end";

        return con.QuerySingleAsync<double?>(new CommandDefinition(query, new { poolId, start, end }, cancellationToken: ct));
    }

    public Task<double?> GetMinerShareDifficultyBetweenAsync(IDbConnection con, string poolId, string miner, DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = "SELECT SUM(difficulty / networkdifficulty) FROM shares WHERE poolid = @poolId AND miner = @miner AND created > @start AND created < @end";

        return con.QuerySingleAsync<double?>(new CommandDefinition(query, new { poolId, miner, start, end }, cancellationToken: ct));
    }

    public async Task<double?> GetEffectiveAccumulatedShareDifficultyBetweenAsync(IDbConnection con, string poolId, DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = @"
        SELECT SUM(difficulty / networkdifficulty) 
        FROM shares 
        WHERE poolid = @poolId 
          AND created >= @start 
          AND created < @end";

        var result = await con.QuerySingleAsync<double?>(
            new CommandDefinition(query, new { poolId, start, end }, cancellationToken: ct));

        return result;
    }

    public async Task<MinerWorkerHashes[]> GetHashAccumulationBetweenAsync(IDbConnection con, string poolId, DateTime start, DateTime end, CancellationToken ct)
    {
        const string query = @"SELECT SUM(difficulty), COUNT(difficulty), MIN(created) AS firstshare, MAX(created) AS lastshare, miner, worker FROM shares
            WHERE poolid = @poolId AND created >= @start AND created <= @end
            GROUP BY miner, worker";

        return (await con.QueryAsync<MinerWorkerHashes>(new CommandDefinition(query, new { poolId, start, end }, cancellationToken: ct)))
            .ToArray();
    }

    public async Task<KeyValuePair<string, double>[]> GetAccumulatedUserAgentShareDifficultyBetweenAsync(
        IDbConnection con, string poolId, DateTime start, DateTime end, bool byVersion, CancellationToken ct)
    {
        const string query = @"SELECT SUM(difficulty) AS value, REGEXP_REPLACE(useragent, '/.+', '') AS key FROM shares
                WHERE poolid = @poolId AND created > @start AND created < @end
                GROUP BY key ORDER BY value DESC";

        const string queryByVersion = @"SELECT SUM(difficulty) AS value, useragent AS key FROM shares
            WHERE poolid = @poolId AND created > @start AND created < @end
            GROUP BY key ORDER BY value DESC";

        return (await con.QueryAsync<KeyValuePair<string, double>>(new CommandDefinition(!byVersion ? query : queryByVersion, new { poolId, start, end }, cancellationToken: ct)))
            .ToArray();
    }

    public async Task<string[]> GetRecentyUsedIpAddressesAsync(IDbConnection con, IDbTransaction tx, string poolId, string miner, CancellationToken ct)
    {
        const string query = @"SELECT DISTINCT s.ipaddress FROM (SELECT * FROM shares
            WHERE poolid = @poolId and miner = @miner ORDER BY CREATED DESC LIMIT 100) s";

        return (await con.QueryAsync<string>(new CommandDefinition(query, new { poolId, miner }, tx, cancellationToken: ct)))
            .ToArray();
    }
}
