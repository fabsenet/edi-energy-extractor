namespace EdiEnergyExtractor
{
    public class EdiConstants
    {
        public static readonly string[] MessageTypes =
        {
            "APERAK", "CONTRL", "IFTSTA", "INSRPT", "INVOIC", "MSCONS", "ORDERS", "ORDRSP", "PRICAT", "QUOTES", "REMADV", "REQOTE", "UTILMD"
        };

        public static readonly string[] EdiProcesses =
        {
            "GPKE GeLi Gas", "MaBiS", "WiM", "Einspeiser", "Netzbetreiberwechsel", "Geschäftsdatenanfrage"
        };
    }
}