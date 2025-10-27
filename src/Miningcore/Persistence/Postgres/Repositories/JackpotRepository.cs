using System;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using NLog;
using System.Numerics;

namespace Miningcore.Persistence.Postgres.Repositories
{
public class JackpotRepository : IJackpotRepository
    {
        private readonly IConnectionFactory connectionFactory;
        private readonly ILogger logger;

        private readonly JackpotsBlockchain blockchain; // Ajout de la blockchain comme dépendance


        public JackpotRepository(IConnectionFactory connectionFactory, ILogger logger, JackpotsBlockchain blockchain)
        {
            this.connectionFactory = connectionFactory;
            this.logger = logger;
            this.blockchain = blockchain;
        }




/*
public async Task<int> GetOrCreateMinerIdAsync(string minerWallet)
{
    const string selectMinerQuery = @"
        SELECT miner_id FROM miners WHERE wallet_address = @minerWallet";

    const string insertMinerQuery = @"
        INSERT INTO miners (wallet_address, join_date)
        VALUES (@minerWallet, NOW())
        ON CONFLICT (wallet_address) DO NOTHING
        RETURNING miner_id";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Vérifie si le mineur existe déjà dans la table miners
    var minerId = await connection.QuerySingleOrDefaultAsync<int>(selectMinerQuery, new { minerWallet });

    if (minerId == 0)
    {
        // Insère un nouveau mineur dans la table miners
        minerId = await connection.ExecuteScalarAsync<int>(insertMinerQuery, new { minerWallet });

        if (minerId == 0)
        {
            throw new Exception("Failed to insert or retrieve miner.");
        }

        // Créé un draw_number pour le nouveau mineur
        await GetOrCreateDrawNumberAsync(minerId);
    }

    return minerId;
}

public async Task<string> GetOrCreateDrawNumberAsync(int minerId)
{
    const string checkCandidatQuery = @"
        SELECT candidat FROM miners WHERE miner_id = @MinerId";

    const string selectDrawNumberQuery = @"
        SELECT draw_number FROM jackpot_entries WHERE miner_id = @MinerId AND eligible = TRUE";

    const string insertJackpotEntryQuery = @"
        INSERT INTO jackpot_entries (miner_id, draw_number, eligible, draw_matched, jackpot_id, created_at)
        VALUES (@MinerId, @DrawNumber, TRUE, FALSE, @DrawNumber, NOW())
        RETURNING draw_number";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Vérifie si le mineur est un candidat
    var isCandidat = await connection.ExecuteScalarAsync<bool>(checkCandidatQuery, new { MinerId = minerId });

    if (!isCandidat)
    {
        logger.Info($"Miner ID {minerId} is not a candidate. Draw number will not be created.");
        return null;
    }

    // Vérifie s'il existe déjà une chaîne de jackpot active
    var existingDrawNumber = await connection.QuerySingleOrDefaultAsync<string>(selectDrawNumberQuery, new { MinerId = minerId });

    if (!string.IsNullOrEmpty(existingDrawNumber))
    {
        return existingDrawNumber;
    }

    // Génère une nouvelle chaîne unique alphanumérique pour draw_number
    var drawNumber = GenerateUniqueDrawNumber();

    // Insère une nouvelle entrée de jackpot avec la chaîne unique et eligible = TRUE
    var newDrawNumber = await connection.ExecuteScalarAsync<string>(insertJackpotEntryQuery, new
    {
        MinerId = minerId,
        DrawNumber = drawNumber
    });

    logger.Info($"New draw number {newDrawNumber} created for miner ID {minerId}.");

    return newDrawNumber;
}

// Génère une chaîne alphanumérique unique pour draw_number
private string GenerateUniqueDrawNumber()
{
    const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
    var random = new Random();
    return new string(Enumerable.Repeat(chars, 32)
        .Select(s => s[random.Next(s.Length)]).ToArray());
}
*/




public async Task<int> GetOrCreateMinerIdAsync(string minerWallet)
{
    const string selectMinerQuery = @"
        SELECT miner_id FROM miners WHERE wallet_address = @minerWallet";

    const string insertMinerQuery = @"
        INSERT INTO miners (wallet_address, join_date)
        VALUES (@minerWallet, NOW())
        ON CONFLICT (wallet_address) DO NOTHING
        RETURNING miner_id";

    using var connection = await connectionFactory.OpenConnectionAsync();

    try
    {
        // Vérifie si le mineur existe déjà dans la table miners
        var minerId = await connection.QuerySingleOrDefaultAsync<int>(selectMinerQuery, new { minerWallet });

        if (minerId == 0)
        {
            // Insère un nouveau mineur dans la table miners
            minerId = await connection.ExecuteScalarAsync<int>(insertMinerQuery, new { minerWallet });

            if (minerId == 0)
            {
                throw new Exception("Failed to insert or retrieve miner.");
            }

            // Créé un draw_number et un jackpot_id pour le nouveau mineur
            await GetOrCreateDrawNumberAsync(minerId);
        }

        return minerId;
    }
    catch (Exception ex)
    {
        logger.Error($"An error occurred while processing miner ID creation: {ex.Message}");
        throw;
    }
}

public async Task<string> GetOrCreateDrawNumberAsync(int minerId)
{
    const string checkCandidatQuery = @"
        SELECT candidat FROM miners WHERE miner_id = @MinerId";

    const string selectDrawNumberQuery = @"
        SELECT draw_number FROM jackpot_entries WHERE miner_id = @MinerId AND eligible = TRUE";

    const string insertJackpotEntryQuery = @"
        INSERT INTO jackpot_entries (miner_id, draw_number, eligible, draw_matched, jackpot_id, created_at)
        VALUES (@MinerId, @DrawNumber, FALSE, FALSE, @JackpotId, NOW())
        RETURNING draw_number";

    try
    {
        using var connection = await connectionFactory.OpenConnectionAsync();

        // Vérifie si le mineur est un candidat
        var isCandidat = await connection.ExecuteScalarAsync<bool>(checkCandidatQuery, new { MinerId = minerId });

        if (!isCandidat)
        {
            logger.Info($"Miner ID {minerId} is not a candidate. Draw number and jackpot ID will not be created.");
            return null;
        }

        // Vérifie s'il existe déjà une chaîne de jackpot active
        var existingDrawNumber = await connection.QuerySingleOrDefaultAsync<string>(selectDrawNumberQuery, new { MinerId = minerId });

        if (!string.IsNullOrEmpty(existingDrawNumber))
        {
            return existingDrawNumber;
        }

        // Génère une nouvelle chaîne unique alphanumérique de 64 caractères pour draw_number
        var drawNumber = GenerateUniqueDrawNumber(64);

        // Génère un jackpot_id unique de 32 caractères
        var jackpotId = GenerateUniqueDrawNumber(32);

        // Insère une nouvelle entrée de jackpot avec le draw_number, le jackpot_id et eligible = TRUE
        var newDrawNumber = await connection.ExecuteScalarAsync<string>(insertJackpotEntryQuery, new
        {
            MinerId = minerId,
            DrawNumber = drawNumber,
            JackpotId = jackpotId
        });

        logger.Info($"New draw number {newDrawNumber} and jackpot ID {jackpotId} created for miner ID {minerId}.");

        return newDrawNumber;
    }
    catch (Exception ex)
    {
        logger.Error($"An error occurred while generating draw number and jackpot ID for miner ID {minerId}: {ex.Message}");
        throw;
    }
}

// Génère une chaîne alphanumérique unique pour draw_number ou jackpot_id de la longueur spécifiée
private string GenerateUniqueDrawNumber(int length)
{
    const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
    var random = new Random();
    return new string(Enumerable.Repeat(chars, length)
        .Select(s => s[random.Next(s.Length)]).ToArray());
}

















        public async Task InsertJackpotEntryAsync(JackpotEntry jackpotEntry)
        {
            const string query = @"
                INSERT INTO jackpot_entries (miner_id, draw_number, eligible, draw_matched, jackpot_id)
                VALUES (@minerId, @drawNumber, @eligible, @drawMatched, @jackpotId)";

            using var connection = await connectionFactory.OpenConnectionAsync();

            await connection.ExecuteAsync(query, new
            {
                minerId = jackpotEntry.MinerId,
                drawNumber = jackpotEntry.DrawNumber,
                eligible = jackpotEntry.Eligible,
                drawMatched = jackpotEntry.DrawMatched,
                jackpotId = jackpotEntry.JackpotId
            });
        }





/*
public async Task UpdateJackpotStringAsync(int minerId)
{
    const string getCurrentChainQuery = @"
        SELECT draw_number, eligible FROM jackpot_entries
        WHERE miner_id = @minerId ORDER BY created_at DESC LIMIT 1";

    const string checkConnectionQuery = @"
        SELECT connect_time, disconnect_time FROM miner_connections
        WHERE miner_id = @minerId
        ORDER BY connect_time DESC LIMIT 1";

    const string updateChainQuery = @"
        UPDATE jackpot_entries
        SET draw_number = LEFT(draw_number, LENGTH(draw_number) - 1)
        WHERE miner_id = @minerId AND eligible = TRUE";

    const string setIneligibleQuery = @"
        UPDATE jackpot_entries
        SET eligible = FALSE
        WHERE miner_id = @minerId AND eligible = TRUE";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Récupère la chaîne actuelle et le statut d'éligibilité
    var currentEntry = await connection.QuerySingleOrDefaultAsync<(string drawNumber, bool eligible)>(getCurrentChainQuery, new { minerId });

    if (currentEntry == default || !currentEntry.eligible)
    {
        logger.Info($"Miner {minerId} is either ineligible or has no current jackpot entry.");
        return;
    }

    var currentChain = currentEntry.drawNumber;

    // Vérifie que la chaîne est supérieure à 4 caractères
    if (currentChain.Length <= 1)
    {
        logger.Info($"Jackpot chain for miner ID {minerId} has reached the minimum length of 4 characters.");
        return;
    }

    // Vérifie la durée de la connexion la plus récente
    var connectionData = await connection.QuerySingleOrDefaultAsync<(DateTime connectTime, DateTime? disconnectTime)>(checkConnectionQuery, new { minerId });

    // Si le mineur est déconnecté, marque le champ eligible à FALSE
    if (connectionData.disconnectTime != null)
    {
        await connection.ExecuteAsync(setIneligibleQuery, new { minerId });
        logger.Info($"Miner {minerId} has been marked ineligible due to disconnection.");
        return;
    }

// Calcule la durée de connexion
var connectionDuration = DateTime.UtcNow - connectionData.connectTime;

// Vérifie si la durée de connexion est d'au moins une heure
if (connectionDuration.TotalMinutes >= 3)
{
    // Raccourcit la chaîne de jackpot toutes les cinq minutes après une heure de connexion
    var minutesSinceLastReduction = (int)(connectionDuration.TotalMinutes - 60) % 1;

    if (minutesSinceLastReduction == 0)
    {
        await connection.ExecuteAsync(updateChainQuery, new { minerId });
        logger.Info($"Jackpot chain shortened for miner ID {minerId}. New chain length: {currentChain.Length - 1}");
    }
    else
    {
        logger.Info($"Miner ID {minerId} does not meet the 5-minute reduction interval.");
    }
}
else
{
    logger.Info($"Miner ID {minerId} has not met the initial one-hour connection requirement.");
}
}
*/



/*
public async Task UpdateJackpotStringAsync(int minerId, BigInteger nonce, string jobIdHex)
{
    const string getCurrentChainQuery = @"
        SELECT draw_number, eligible FROM jackpot_entries
        WHERE miner_id = @minerId ORDER BY created_at DESC LIMIT 1";

    const string checkConnectionQuery = @"
        SELECT connect_time, disconnect_time FROM miner_connections
        WHERE miner_id = @minerId
        ORDER BY connect_time DESC LIMIT 1";

    const string updateChainQuery = @"
        UPDATE jackpot_entries
        SET draw_number = LEFT(draw_number, LENGTH(draw_number) - @reductionCount)
        WHERE miner_id = @minerId AND eligible = TRUE";

    const string setIneligibleQuery = @"
        UPDATE jackpot_entries
        SET eligible = FALSE
        WHERE miner_id = @minerId AND eligible = TRUE";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Récupère la chaîne actuelle et le statut d'éligibilité
    var currentEntry = await connection.QuerySingleOrDefaultAsync<(string drawNumber, bool eligible)>(getCurrentChainQuery, new { minerId });

    if (currentEntry == default || !currentEntry.eligible)
    {
        logger.Info($"Miner {minerId} is either ineligible or has no current jackpot entry.");
        return;
    }

    var currentChain = currentEntry.drawNumber;

    // Conversion du jobIdHex en BigInteger
    BigInteger jobId = BigInteger.Parse(jobIdHex, System.Globalization.NumberStyles.HexNumber);

    // Calcul du nombre de caractères à réduire en fonction de la différence entre le nonce et le jobId
    int reductionCount = Math.Max(1, (int)(nonce - jobId)); // Assure que la réduction est au minimum de 1 caractère

    // Vérifie si la chaîne est supérieure à 4 caractères après la réduction
    if (currentChain.Length <= reductionCount)
    {
        await connection.ExecuteAsync(setIneligibleQuery, new { minerId });
        logger.Info($"Miner {minerId} jackpot chain has reached the minimum length and is now ineligible.");
        return;
    }




    // Vérifie la durée de la connexion la plus récente
    var connectionData = await connection.QuerySingleOrDefaultAsync<(DateTime connectTime, DateTime? disconnectTime)>(checkConnectionQuery, new { minerId });

    // Si le mineur est déconnecté, marque le champ eligible à FALSE
    if (connectionData.disconnectTime != null)
    {
        await connection.ExecuteAsync(setIneligibleQuery, new { minerId });
        logger.Info($"Miner {minerId} has been marked ineligible due to disconnection.");
        return;
    }






    // Met à jour le draw_number avec la réduction calculée
    await connection.ExecuteAsync(updateChainQuery, new { minerId, reductionCount });
    logger.Info($"Jackpot chain shortened by {reductionCount} characters for miner ID {minerId}. New chain length: {currentChain.Length - reductionCount}");
}
*/


/*
public async Task UpdateJackpotStringAsync(int minerId, BigInteger nonce, string jobIdHex)
{
    const string getCurrentChainQuery = @"
        SELECT draw_number, eligible FROM jackpot_entries
        WHERE miner_id = @minerId ORDER BY created_at DESC LIMIT 1";

    const string checkConnectionQuery = @"
        SELECT connect_time, disconnect_time FROM miner_connections
        WHERE miner_id = @minerId
        ORDER BY connect_time DESC LIMIT 1";

    const string updateChainQuery = @"
        UPDATE jackpot_entries
        SET draw_number = LEFT(draw_number, LENGTH(draw_number) - @reductionCount)
        WHERE miner_id = @minerId AND eligible = TRUE";

    const string setIneligibleQuery = @"
        UPDATE jackpot_entries
        SET eligible = FALSE
        WHERE miner_id = @minerId AND eligible = TRUE";

    const string updateMinerQuery = @"
        UPDATE miners
        SET last_share_date = NOW(), last_connected_date = @connectTime, last_jackpot_id = @jackpotId
        WHERE miner_id = @minerId";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Récupère la chaîne actuelle et le statut d'éligibilité
    var currentEntry = await connection.QuerySingleOrDefaultAsync<(string drawNumber, bool eligible)>(getCurrentChainQuery, new { minerId });

    if (currentEntry == default || !currentEntry.eligible)
    {
        logger.Info($"Miner {minerId} is either ineligible or has no current jackpot entry.");
        return;
    }

    var currentChain = currentEntry.drawNumber;

    // Conversion du jobIdHex en BigInteger
    BigInteger jobId = BigInteger.Parse(jobIdHex, System.Globalization.NumberStyles.HexNumber);

    // Calcul du nombre de caractères à réduire en fonction de la différence entre le nonce et le jobId
    int reductionCount = Math.Max(1, (int)(nonce - jobId)); // Assure que la réduction est au minimum de 1 caractère

    // Vérifie si la chaîne est supérieure à 4 caractères après la réduction
    if (currentChain.Length <= reductionCount)
    {
        await connection.ExecuteAsync(setIneligibleQuery, new { minerId });
        logger.Info($"Miner {minerId} jackpot chain has reached the minimum length and is now ineligible.");
        return;
    }

    // Vérifie la durée de la connexion la plus récente
    var connectionData = await connection.QuerySingleOrDefaultAsync<(DateTime connectTime, DateTime? disconnectTime)>(checkConnectionQuery, new { minerId });

    // Si le mineur est déconnecté, marque le champ eligible à FALSE
    if (connectionData.disconnectTime != null)
    {
        await connection.ExecuteAsync(setIneligibleQuery, new { minerId });
        logger.Info($"Miner {minerId} has been marked ineligible due to disconnection.");
        return;
    }

    // Met à jour le draw_number avec la réduction calculée
    await connection.ExecuteAsync(updateChainQuery, new { minerId, reductionCount });
    logger.Info($"Jackpot chain shortened by {reductionCount} characters for miner ID {minerId}. New chain length: {currentChain.Length - reductionCount}");

    // Met à jour les informations du mineur dans la table miners
    var jackpotId = await connection.QuerySingleOrDefaultAsync<string>("SELECT jackpot_id FROM jackpot_entries WHERE miner_id = @minerId AND eligible = TRUE ORDER BY created_at DESC LIMIT 1", new { minerId });

    await connection.ExecuteAsync(updateMinerQuery, new
    {
        minerId,
        connectTime = connectionData.connectTime,
        jackpotId
    });

    logger.Info($"Miner {minerId} details updated: last_share_date, last_connected_date, and last_jackpot_id.");
}
*/
/*
public async Task UpdateJackpotStringAsync(int minerId, BigInteger nonce, string jobIdHex)
{
    const string getCurrentChainQuery = @"
        SELECT draw_number, eligible FROM jackpot_entries
        WHERE miner_id = @minerId ORDER BY created_at DESC LIMIT 1";

    const string checkConnectionQuery = @"
        SELECT connect_time, disconnect_time FROM miner_connections
        WHERE miner_id = @minerId
        ORDER BY connect_time DESC LIMIT 1";

    const string updateChainQuery = @"
        UPDATE jackpot_entries
        SET draw_number = LEFT(draw_number, LENGTH(draw_number) - @reductionCount) || '0'
        WHERE miner_id = @minerId AND eligible = TRUE";

    const string setIneligibleQuery = @"
        UPDATE jackpot_entries
        SET eligible = FALSE
        WHERE miner_id = @minerId AND eligible = TRUE";

    const string updateMinerQuery = @"
        UPDATE miners
        SET last_share_date = NOW(), last_connected_date = @connectTime, last_jackpot_id = @jackpotId
        WHERE miner_id = @minerId";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Récupère la chaîne actuelle et le statut d'éligibilité
    var currentEntry = await connection.QuerySingleOrDefaultAsync<(string drawNumber, bool eligible)>(getCurrentChainQuery, new { minerId });

    if (currentEntry == default || !currentEntry.eligible)
    {
        logger.Info($"Miner {minerId} is either ineligible or has no current jackpot entry.");
        return;
    }

    var currentChain = currentEntry.drawNumber;

    // Conversion du jobIdHex en BigInteger
    BigInteger jobId = BigInteger.Parse(jobIdHex, System.Globalization.NumberStyles.HexNumber);

    // Calcul du nombre de caractères à réduire en fonction de la différence entre le nonce et le jobId
    int reductionCount = Math.Max(1, (int)(nonce - jobId)); // Assure que la réduction est au minimum de 1 caractère

    // Vérifie si la chaîne est supérieure à 4 caractères après la réduction
    if (currentChain.Length <= reductionCount)
    {
        await connection.ExecuteAsync(setIneligibleQuery, new { minerId });
        logger.Info($"Miner {minerId} jackpot chain has reached the minimum length and is now ineligible.");
        return;
    }

    // Vérifie la durée de la connexion la plus récente
    var connectionData = await connection.QuerySingleOrDefaultAsync<(DateTime connectTime, DateTime? disconnectTime)>(checkConnectionQuery, new { minerId });

    // Si le mineur est déconnecté, marque le champ eligible à FALSE
    if (connectionData.disconnectTime != null)
    {
        await connection.ExecuteAsync(setIneligibleQuery, new { minerId });
        logger.Info($"Miner {minerId} has been marked ineligible due to disconnection.");
        return;
    }

    // Met à jour le draw_number avec la réduction calculée et ajoute un "0" si reductionCount > 0
    if (reductionCount > 0)
    {
        await connection.ExecuteAsync(updateChainQuery, new { minerId, reductionCount });
        logger.Info($"Jackpot chain shortened by {reductionCount} characters and '0' added for miner ID {minerId}. New chain length: {currentChain.Length - reductionCount + 1}");
    }

    // Met à jour les informations du mineur dans la table miners
    var jackpotId = await connection.QuerySingleOrDefaultAsync<string>("SELECT jackpot_id FROM jackpot_entries WHERE miner_id = @minerId AND eligible = TRUE ORDER BY created_at DESC LIMIT 1", new { minerId });

    await connection.ExecuteAsync(updateMinerQuery, new
    {
        minerId,
        connectTime = connectionData.connectTime,
        jackpotId
    });

    logger.Info($"Miner {minerId} details updated: last_share_date, last_connected_date, and last_jackpot_id.");
}
*/
/*
public async Task UpdateJackpotStringAsync(int minerId, BigInteger nonce, string jobIdHex)
{
    const string getCurrentChainQuery = @"
        SELECT draw_number, eligible FROM jackpot_entries
        WHERE miner_id = @minerId ORDER BY created_at DESC LIMIT 1";

    const string checkConnectionQuery = @"
        SELECT connect_time, disconnect_time FROM miner_connections
        WHERE miner_id = @minerId
        ORDER BY connect_time DESC LIMIT 1";

    const string updateChainQuery = @"
        UPDATE jackpot_entries
        SET draw_number = @newDrawNumber, eligible = @isEligible
        WHERE miner_id = @minerId";

    const string updateMinerQuery = @"
        UPDATE miners
        SET last_share_date = NOW(), last_connected_date = @connectTime, last_jackpot_id = @jackpotId
        WHERE miner_id = @minerId";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Récupère la chaîne actuelle et le statut d'éligibilité
    var currentEntry = await connection.QuerySingleOrDefaultAsync<(string drawNumber, bool eligible)>(getCurrentChainQuery, new { minerId });

    if (currentEntry == default)
    {
        logger.Info($"Miner {minerId} has no current jackpot entry.");
        return;
    }

    var currentChain = currentEntry.drawNumber;
    bool isEligible = false;

    // Réduction du nonce par addition successive de ses chiffres
    int reducedNonce = ReduceToSingleDigit(nonce);

    // Conversion du jobIdHex en BigInteger et réduction par addition successive
    BigInteger jobId = BigInteger.Parse(jobIdHex, System.Globalization.NumberStyles.HexNumber);
    int reducedBlockheight = ReduceToSingleDigit(jobId);

    // Calcul de la différence entre les valeurs réduites de nonce et blockheight
    int difference = reducedNonce - reducedBlockheight;

    // Vérifie si la longueur de la chaîne est déjà de 4 ou 5
    if (currentChain.Length == 2 || currentChain.Length == 3)
    {
        isEligible = true;
    }
    else if (difference < 0)
    {
        // Réduit le nombre de caractères si différence négative, sans passer sous 4 caractères
        int reductionCount = Math.Min(currentChain.Length - 2, Math.Abs(difference));
        currentChain = currentChain.Substring(0, currentChain.Length - reductionCount);
    }
    else
    {
        // Ajoute un '0' à la fin si différence positive
        currentChain += "0";
    }

    // Re-vérifie la longueur pour déterminer l'éligibilité
    if (currentChain.Length == 3 || currentChain.Length == 3)
    {
        isEligible = true;
    }

    // Mise à jour de la chaîne et de l'éligibilité
    await connection.ExecuteAsync(updateChainQuery, new { minerId, newDrawNumber = currentChain, isEligible });
    logger.Info($"Miner {minerId} jackpot chain updated. New chain: {currentChain}, Eligible: {isEligible}");

    // Met à jour les informations du mineur dans la table miners, indépendamment de l'éligibilité
    var jackpotId = await connection.QuerySingleOrDefaultAsync<string>("SELECT jackpot_id FROM jackpot_entries WHERE miner_id = @minerId ORDER BY created_at DESC LIMIT 1", new { minerId });

    await connection.ExecuteAsync(updateMinerQuery, new
    {
        minerId,
        connectTime = DateTime.UtcNow,
        jackpotId
    });

    logger.Info($"Miner {minerId} details updated: last_share_date, last_connected_date, and last_jackpot_id.");
}
*/

public async Task UpdateJackpotStringAsync(int minerId)
{
    const string getCurrentChainQuery = @"
        SELECT draw_number, eligible FROM jackpot_entries
        WHERE miner_id = @minerId ORDER BY created_at DESC LIMIT 1";

    const string updateChainQuery = @"
        UPDATE jackpot_entries
        SET draw_number = @newDrawNumber, eligible = @isEligible
        WHERE miner_id = @minerId";

    const string updateMinerQuery = @"
        UPDATE miners
        SET last_share_date = NOW(), last_connected_date = @connectTime, last_jackpot_id = @jackpotId
        WHERE miner_id = @minerId";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Récupère la chaîne actuelle et le statut d'éligibilité
    var currentEntry = await connection.QuerySingleOrDefaultAsync<(string drawNumber, bool eligible)>(getCurrentChainQuery, new { minerId });

    if (currentEntry == default)
    {
        logger.Info($"Miner {minerId} has no current jackpot entry.");
        return;
    }

    var currentChain = currentEntry.drawNumber;
    bool isEligible = false;

    // Calcul de la différence (blockheight est toujours 1)
    int reducedBlockheight = 1;
    int difference = 0 - reducedBlockheight;

    // Vérifie si la longueur de la chaîne est déjà de 4 ou 5 pour l'éligibilité
    if (currentChain.Length == 4 || currentChain.Length == 5)
    {
        isEligible = true;
    }
    else if (difference < 0)
    {
        // Réduction du nombre de caractères sans passer sous 4
        int reductionCount = Math.Min(currentChain.Length - 4, Math.Abs(difference));
        if (reductionCount > 0 && reductionCount < currentChain.Length)
            currentChain = currentChain.Substring(0, currentChain.Length - reductionCount);
    }
    else
    {
        // Ajoute un '0' à la fin si différence positive et longueur maximale de 5 caractères
        if (currentChain.Length < 5)
            currentChain += "0";
    }

    // Re-vérifie la longueur pour déterminer l'éligibilité
    if (currentChain.Length == 4 || currentChain.Length == 5)
    {
        isEligible = true;
    }

    // Mise à jour de la chaîne et de l'éligibilité
    await connection.ExecuteAsync(updateChainQuery, new { minerId, newDrawNumber = currentChain, isEligible });
    logger.Info($"Miner {minerId} jackpot chain updated. New chain: {currentChain}, Eligible: {isEligible}");

    // Met à jour les informations du mineur dans la table miners
    var jackpotId = await connection.QuerySingleOrDefaultAsync<string>("SELECT jackpot_id FROM jackpot_entries WHERE miner_id = @minerId ORDER BY created_at DESC LIMIT 1", new { minerId });

    await connection.ExecuteAsync(updateMinerQuery, new
    {
        minerId,
        connectTime = DateTime.UtcNow,
        jackpotId
    });

    logger.Info($"Miner {minerId} details updated: last_share_date, last_connected_date, and last_jackpot_id.");
}

























/*
public async Task<bool> CheckJackpotWinAsync(string blockHash, int minerId)
{
    const string query = @"SELECT draw_number FROM jackpot_entries WHERE miner_id = @minerId AND eligible = TRUE";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Récupère le draw_number actuel pour le mineur
    var drawNumber = await connection.QuerySingleOrDefaultAsync<string>(query, new { minerId });

    if (drawNumber == null || drawNumber.Length < 1)
    {
        logger.Info($"[P4M] No valid draw number found for miner {minerId}, or draw number is too short.");
        return false;
    }

    // Prend les 4 derniers caractères du draw_number pour la vérification
    var lastFourChars = drawNumber[^1..]; // Utilise une tranche pour obtenir les 4 derniers caractères


    logger.Info($"[P4M] Jackpot match found for miner {minerId} with jackpot string {lastFourChars} in block hash {blockHash}");


    // Vérifie si les 4 derniers caractères correspondent à une partie du blockHash
    bool hasWon = blockHash.Contains(lastFourChars);
    if (hasWon)
    {
        logger.Info($"[P4M] Jackpot match found for miner {minerId} with jackpot string {lastFourChars} in block hash {blockHash}");
    }

    return hasWon;
}
*/
public async Task<bool> CheckJackpotWinAsync(string blockHash, int minerId)
{
    const string query = @"SELECT draw_number FROM jackpot_entries WHERE miner_id = @minerId AND eligible = TRUE";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Récupère le draw_number actuel pour le mineur
    var drawNumber = await connection.QuerySingleOrDefaultAsync<string>(query, new { minerId });

    if (string.IsNullOrEmpty(drawNumber) || (drawNumber.Length < 4 || drawNumber.Length > 5))
    {
        logger.Info($"[P4M] No valid draw number of length 4 or 5 found for miner {minerId}.");
        return false;
    }

    // Prend les 4 ou 5 derniers caractères du draw_number pour la vérification, selon sa longueur
    string jackpotString = drawNumber.Length == 4 ? drawNumber[^4..] : drawNumber[^5..];

    // Vérifie si les 4 ou 5 derniers caractères correspondent à une partie du blockHash
    bool hasWon = blockHash.Contains(jackpotString);
    if (hasWon)
    {
        logger.Info($"[P4M] Jackpot match found for miner {minerId} with jackpot string {jackpotString} in block hash {blockHash}");
    }
    else
    {
        logger.Info($"[P4M] No jackpot match for miner {minerId} with jackpot string {jackpotString} in block hash {blockHash}");
    }

    return hasWon;
}




























/*
public async Task RegisterJackpotWinAsync(string poolId, int minerId, string blockHash)
{
    // Étape 1 : Récupérer le jackpot_id existant de jackpot_entries pour le miner_id
    var jackpotId = await GetJackpotIdForMinerAsync(minerId);

    if (jackpotId == null)
    {
        logger.Warn($"Aucune entrée de jackpot trouvée pour le miner ID {minerId}. Enregistrement du gain impossible.");
        return;
    }

    // Étape 2 : Utiliser jackpotId pour créer une entrée dans jackpots
    var jackpot = new Jackpot
    {
        JackpotId = jackpotId,  // jackpotId est maintenant une chaîne
        PoolId = poolId,
        Amount = await GetLatestRewardAmountAsync(poolId),
        RewardedAt = DateTime.UtcNow,
        BlockHash = blockHash
    };

    // Insérer une nouvelle entrée dans la table jackpots avec le jackpot_id récupéré de jackpot_entries
    await InsertJackpotAsync(jackpot);

    // Mettre à jour jackpot_entries pour indiquer le gain
    const string updateJackpotEntryQuery = @"
        UPDATE jackpot_entries
        SET draw_matched = TRUE
        WHERE miner_id = @MinerId AND jackpot_id = @JackpotId";

    using var connection = await connectionFactory.OpenConnectionAsync();
    await connection.ExecuteAsync(updateJackpotEntryQuery, new
    {
        MinerId = minerId,
        JackpotId = jackpotId  // jackpotId doit être de type string ici
    });

    logger.Info($"Gain de jackpot enregistré pour le miner ID {minerId} avec le hash de bloc {blockHash} et l'ID de jackpot {jackpotId}");
}
*/
public async Task RegisterJackpotWinAsync(string poolId, int minerId, string blockHash)
{
    // Étape 1 : Récupérer le jackpot_id existant et les informations supplémentaires pour le miner_id
    var jackpotId = await GetJackpotIdForMinerAsync(minerId);

    if (jackpotId == null)
    {
        logger.Warn($"Aucune entrée de jackpot trouvée pour le miner ID {minerId}. Enregistrement du gain impossible.");
        return;
    }

    // Récupérer l'adresse de portefeuille et le draw_number pour le miner
    var walletAddress = await GetWalletAddressForMinerAsync(minerId);
    var drawNumber = await GetDrawNumberForMinerAsync(minerId);

    // Étape 2 : Utiliser jackpotId pour créer une entrée dans jackpots
    var jackpot = new Jackpot
    {
        JackpotId = jackpotId,
        PoolId = poolId,
        Amount = await GetLatestRewardAmountAsync(poolId),
        RewardedAt = DateTime.UtcNow,
        BlockHash = blockHash,
        WalletAddress = walletAddress,
        DrawNumber = drawNumber
    };

    // Insérer une nouvelle entrée dans la table jackpots avec les informations récupérées
    await InsertJackpotAsync(jackpot);

    // Mettre à jour jackpot_entries pour indiquer le gain
    const string updateJackpotEntryQuery = @"
        UPDATE jackpot_entries
        SET draw_matched = TRUE
        WHERE miner_id = @MinerId AND jackpot_id = @JackpotId";

    using var connection = await connectionFactory.OpenConnectionAsync();
    await connection.ExecuteAsync(updateJackpotEntryQuery, new
    {
        MinerId = minerId,
        JackpotId = jackpotId
    });

    logger.Info($"Gain de jackpot enregistré pour le miner ID {minerId} avec le hash de bloc {blockHash} et l'ID de jackpot {jackpotId}");
}


















public async Task<string?> GetJackpotIdForMinerAsync(int minerId)
{
    const string selectJackpotIdQuery = @"
        SELECT jackpot_id FROM jackpot_entries
        WHERE miner_id = @MinerId AND eligible = TRUE
        ORDER BY created_at DESC LIMIT 1";

    using var connection = await connectionFactory.OpenConnectionAsync();
    return await connection.QuerySingleOrDefaultAsync<string>(selectJackpotIdQuery, new { MinerId = minerId });
}


/*
public async Task InsertJackpotAsync(Jackpot jackpot)
{
    const string insertJackpotQuery = @"
        INSERT INTO jackpots (jackpot_id, pool_id, amount, rewarded_at, blockHash)
        VALUES (@JackpotId, @PoolId, @Amount, @RewardedAt, @BlockHash)";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Insère un nouveau jackpot en utilisant le jackpot_id récupéré de jackpot_entries
    await connection.ExecuteAsync(insertJackpotQuery, new
    {
        jackpot.JackpotId,
        jackpot.PoolId,
        jackpot.Amount,
        jackpot.RewardedAt,
        jackpot.BlockHash
    });
}
*/

/*
public async Task InsertJackpotAsync(Jackpot jackpot)
{
    const string checkExistenceQuery = @"
        SELECT COUNT(1) FROM jackpots WHERE jackpot_id = @JackpotId";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Vérifie si le jackpot existe déjà
    var exists = await connection.ExecuteScalarAsync<int>(checkExistenceQuery, new { jackpot.JackpotId });
    if (exists > 0)
    {
        logger.Warn($"Jackpot avec l'ID {jackpot.JackpotId} existe déjà. Insertion annulée.");
        return;
    }

    const string insertJackpotQuery = @"
        INSERT INTO jackpots (jackpot_id, pool_id, amount, rewarded_at, blockHash, wallet_address, draw_number)
        VALUES (@JackpotId, @PoolId, @Amount, @RewardedAt, @BlockHash, @WalletAddress, @DrawNumber)";

    // Insère un nouveau jackpot uniquement si l'ID est unique
    await connection.ExecuteAsync(insertJackpotQuery, new
    {
        jackpot.JackpotId,
        jackpot.PoolId,
        jackpot.Amount,
        jackpot.RewardedAt,
        jackpot.BlockHash,
        jackpot.WalletAddress,
        jackpot.DrawNumber
    });

    logger.Info($"Jackpot inséré avec succès pour l'ID {jackpot.JackpotId}.");
}
*/



    public async Task InsertJackpotAsync(Jackpot jackpot)
    {
        const string checkExistenceQuery = @"
            SELECT COUNT(1) FROM jackpots WHERE jackpot_id = @JackpotId";

        using var connection = await connectionFactory.OpenConnectionAsync();

        // Vérifie si le jackpot existe déjà
        var exists = await connection.ExecuteScalarAsync<int>(checkExistenceQuery, new { jackpot.JackpotId });
        if (exists > 0)
        {
            logger.Warn($"Jackpot avec l'ID {jackpot.JackpotId} existe déjà. Insertion annulée.");
            return;
        }

        const string insertJackpotQuery = @"
            INSERT INTO jackpots (jackpot_id, pool_id, amount, rewarded_at, blockHash, wallet_address, draw_number)
            VALUES (@JackpotId, @PoolId, @Amount, @RewardedAt, @BlockHash, @WalletAddress, @DrawNumber)";

        // Insère un nouveau jackpot uniquement si l'ID est unique
        await connection.ExecuteAsync(insertJackpotQuery, new
        {
            jackpot.JackpotId,
            jackpot.PoolId,
            jackpot.Amount,
            jackpot.RewardedAt,
            jackpot.BlockHash,
            jackpot.WalletAddress,
            jackpot.DrawNumber
        });

        logger.Info($"Jackpot inséré avec succès pour l'ID {jackpot.JackpotId}.");

        // Ajoute un nouveau bloc à la blockchain pour le jackpot inséré
        await blockchain.AddBlockAsync(jackpot, connectionFactory, logger);
    }















public async Task UpdateJackpotIfBlockExistsAsync(string blockHash, decimal blockReward, string poolId, ulong blockHeight)
{
    const string checkJackpotQuery = @"
        SELECT jackpot_id FROM jackpots WHERE blockHash = @BlockHash";

    const string getMinerIdQuery = @"
        SELECT miner_id FROM jackpot_entries WHERE jackpot_id = @JackpotId LIMIT 1";

    const string getWalletAddressQuery = @"
        SELECT wallet_address FROM miners WHERE miner_id = @MinerId";

    const string updateJackpotQuery = @"
        UPDATE jackpots
        SET amount = @RewardAmount, rewarded_at = NOW()
        WHERE blockHash = @BlockHash";

    const string insertPoolRewardQuery = @"
        INSERT INTO pool_rewards (pool_id, block_height, reward_amount, rewarded_at, wallet_address)
        VALUES (@PoolId, @BlockHeight, @RewardAmount, NOW(), @WalletAddress)";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Vérifie si le blockHash existe dans jackpots et récupère jackpot_id
    var jackpotId = await connection.QuerySingleOrDefaultAsync<int?>(checkJackpotQuery, new { BlockHash = blockHash });

    if (jackpotId.HasValue)
    {
        // Si le jackpot_id existe, met à jour le jackpot
        await connection.ExecuteAsync(updateJackpotQuery, new
        {
            RewardAmount = blockReward,
            BlockHash = blockHash
        });

        // Récupère minerId depuis jackpot_entries en utilisant jackpotId
        var minerId = await connection.QuerySingleOrDefaultAsync<int?>(getMinerIdQuery, new { JackpotId = jackpotId.Value });

        if (minerId.HasValue)
        {
            // Récupère wallet_address depuis miners en utilisant minerId
            var walletAddress = await connection.QuerySingleOrDefaultAsync<string>(getWalletAddressQuery, new { MinerId = minerId.Value });

            if (!string.IsNullOrEmpty(walletAddress))
            {
                // Insère une nouvelle entrée dans pool_rewards avec le wallet_address récupéré
                await connection.ExecuteAsync(insertPoolRewardQuery, new
                {
                    PoolId = poolId,
                    BlockHeight = blockHeight,
                    RewardAmount = blockReward,
                    WalletAddress = walletAddress
                });

                logger.Info($"Jackpot updated and pool reward added for block hash {blockHash} with reward {blockReward} and wallet {walletAddress}.");
            }
            else
            {
                logger.Warn($"No wallet address found for miner ID {minerId.Value}. Pool reward entry not added.");
            }
        }
        else
        {
            logger.Warn($"No miner ID found in jackpot_entries for jackpot ID {jackpotId.Value}. Pool reward entry not added.");
        }
    }
    else
    {
        logger.Info($"No matching jackpot entry found for block hash {blockHash}. No update necessary.");
    }
}




public async Task<List<Balance>> GetPendingPaymentsAsync()
{
    const string query = @"
        SELECT pool_id AS PoolId, wallet_address AS Address, reward_amount AS Amount
        FROM pool_rewards
        WHERE payed = FALSE";

    using var connection = await connectionFactory.OpenConnectionAsync();
    return (await connection.QueryAsync<Balance>(query)).ToList();
}

public async Task MarkPaymentsAsPaidAsync(List<Balance> balances)
{
    const string updatePoolRewardsQuery = @"
        UPDATE pool_rewards
        SET payed = TRUE, payment_date = NOW()
        WHERE wallet_address = @Address AND pool_id = @PoolId AND reward_amount = @Amount";

    const string updateMinersQuery = @"
        UPDATE miners
        SET last_jackpot_date = NOW(),
            last_jackpot_id = @JackpotId,
            last_jackpot_amount = @Amount,
            candidate = FALSE,
        WHERE wallet_address = @Address";

    using var connection = await connectionFactory.OpenConnectionAsync();

    // Met à jour les paiements dans pool_rewards
    await connection.ExecuteAsync(updatePoolRewardsQuery, balances);

    // Met à jour les informations de jackpot dans miners
    foreach (var balance in balances)
    {
        // Récupère l'identifiant du jackpot pour chaque paiement, en supposant que `JackpotId` est une propriété de `Balance`
        var jackpotId = await connection.ExecuteScalarAsync<string>("SELECT jackpot_id FROM pool_rewards WHERE wallet_address = @Address AND pool_id = @PoolId", balance);

        await connection.ExecuteAsync(updateMinersQuery, new
        {
            JackpotId = jackpotId,
            Address = balance.Address,
            Amount = balance.Amount
        });
    }
}







public async Task<string?> GetWalletAddressForMinerAsync(int minerId)
{
    const string query = @"
        SELECT wallet_address FROM miners
        WHERE miner_id = @MinerId";

    using var connection = await connectionFactory.OpenConnectionAsync();
    return await connection.QuerySingleOrDefaultAsync<string>(query, new { MinerId = minerId });
}


public async Task<string?> GetDrawNumberForMinerAsync(int minerId)
{
    const string query = @"
        SELECT draw_number FROM jackpot_entries
        WHERE miner_id = @MinerId AND eligible = TRUE
        ORDER BY created_at DESC LIMIT 1";

    using var connection = await connectionFactory.OpenConnectionAsync();
    return await connection.QuerySingleOrDefaultAsync<string>(query, new { MinerId = minerId });
}

















        private async Task<decimal> GetLatestRewardAmountAsync(string poolId)
        {
            const string rewardQuery = @"
                SELECT reward_amount FROM pool_rewards WHERE pool_id = @poolId ORDER BY rewarded_at DESC LIMIT 1";

            using var connection = await connectionFactory.OpenConnectionAsync();

            return await connection.ExecuteScalarAsync<decimal>(rewardQuery, new { poolId });
        }

public async Task SavePoolRewardAsync(string poolId, string walletAddress, int blockHeight)
{
    const string insertPoolRewardQuery = @"
        INSERT INTO pool_rewards (pool_id, wallet_address, block_height, reward_amount, payment_date)
        VALUES (@PoolId, @WalletAddress, @BlockHeight, @RewardAmount, NOW())";

    using var connection = await connectionFactory.OpenConnectionAsync();

    await connection.ExecuteAsync(insertPoolRewardQuery, new
    {
        PoolId = poolId,
        WalletAddress = walletAddress,
        BlockHeight = blockHeight,
        RewardAmount = await GetLatestRewardAmountAsync(poolId) // Assuming reward amount is fetched here
    });
}







/*
public async Task<IEnumerable<Jackpot>> GetJackpotsAsync()
{
    const string query = @"
        SELECT jackpot_id, pool_id, amount, rewarded_at, blockHash, wallet_address, draw_number
        FROM jackpots";

    using var connection = await connectionFactory.OpenConnectionAsync();
    return await connection.QueryAsync<Jackpot>(query);
}*/
public async Task<IEnumerable<Jackpot>> GetJackpotsAsync()
{
    const string query = @"
        SELECT jackpot_id AS JackpotId, pool_id AS PoolId, amount AS Amount,
       rewarded_at AS RewardedAt, blockHash AS BlockHash,
       wallet_address AS WalletAddress, draw_number AS DrawNumber
FROM jackpots;";

    using var connection = await connectionFactory.OpenConnectionAsync();
    return await connection.QueryAsync<Jackpot>(query);
}






    }
}
