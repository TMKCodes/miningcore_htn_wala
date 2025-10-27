using System.IO;
using System.Text.Json;
using Miningcore.Persistence;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using NLog;
using System.Numerics;
using Miningcore.Persistence.Postgres.Repositories;

public class JackpotsBlockchain
{
    public List<JackpotsBlock> Chain { get; private set; }
    private readonly string storageDirectory;

    // Constructeur avec un répertoire de stockage par défaut
    public JackpotsBlockchain() : this("/home/elvalere/Pools/Blockchain")
    {
    }

    // Constructeur qui accepte un chemin de répertoire personnalisé
    public JackpotsBlockchain(string directory)
    {
        storageDirectory = directory;

        // Crée le répertoire de stockage s'il n'existe pas
        if (!Directory.Exists(storageDirectory))
            Directory.CreateDirectory(storageDirectory);

        Chain = LoadBlockchain();
    }

    // Crée le bloc d'origine (genesis block)
    private JackpotsBlock CreateGenesisBlock()
    {
        var genesisBlock = new JackpotsBlock(0, "0", new Jackpot { JackpotId = "genesis", BlockHash = "0" });
        SaveBlock(genesisBlock);
        return genesisBlock;
    }

    // Charge la blockchain depuis le répertoire de stockage
    private List<JackpotsBlock> LoadBlockchain()
    {
        var blocks = new List<JackpotsBlock>();

        foreach (var filePath in Directory.GetFiles(storageDirectory, "*.json"))
        {
            var blockJson = File.ReadAllText(filePath);
            var block = JsonSerializer.Deserialize<JackpotsBlock>(blockJson);
            if (block != null) blocks.Add(block);
        }

        if (blocks.Count == 0)
            blocks.Add(CreateGenesisBlock());

        return blocks;
    }

    // Sauvegarde un bloc sous forme de fichier JSON
    private void SaveBlock(JackpotsBlock block)
    {
        var filePath = Path.Combine(storageDirectory, $"block_{block.Index}.json");
        var blockJson = JsonSerializer.Serialize(block);
        File.WriteAllText(filePath, blockJson);
    }

    // Ajoute un nouveau bloc et sauvegarde
    public async Task AddBlockAsync(Jackpot jackpot, IConnectionFactory connectionFactory, ILogger logger)
    {
        var previousBlock = GetLatestBlock();
        var newBlock = new JackpotsBlock(previousBlock.Index + 1, previousBlock.Hash, jackpot);

        var jackpotRepo = new JackpotRepository(connectionFactory, logger, this);
        await jackpotRepo.InsertJackpotAsync(jackpot);

        Chain.Add(newBlock);
        SaveBlock(newBlock);  // Sauvegarde le bloc dans le répertoire
        logger.Info($"Nouveau bloc ajouté avec ID: {newBlock.JackpotData.JackpotId} et hash: {newBlock.Hash}");
    }

    public JackpotsBlock GetLatestBlock()
    {
        return Chain.Last();
    }

    public bool IsChainValid()
    {
        for (int i = 1; i < Chain.Count; i++)
        {
            var currentBlock = Chain[i];
            var previousBlock = Chain[i - 1];

            if (currentBlock.Hash != currentBlock.JackpotData.BlockHash)
                return false;

            if (currentBlock.PreviousHash != previousBlock.Hash)
                return false;
        }
        return true;
    }
}
