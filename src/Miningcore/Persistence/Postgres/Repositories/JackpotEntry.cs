namespace Miningcore.Persistence.Model
{
    public class JackpotEntry
    {
        public int MinerId { get; set; }
        public string DrawNumber { get; set; }
        public bool Eligible { get; set; }
        public bool DrawMatched { get; set; }
        public string JackpotId { get; set; } // Référence à un jackpot spécifique
    }
}
