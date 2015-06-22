using System.Collections.Generic;

namespace Fabsenet.EdiEnergy
{
    public class EdiConstants
    {
        public static readonly string[] MessageTypes =
        {
            "APERAK", "CONTRL", "IFTSTA", "INSRPT", "INVOIC", "MSCONS", "ORDERS", "ORDRSP", "PRICAT", "QUOTES", "REMADV", "REQOTE", "UTILMD"
        };

        /// <summary>
        /// Key = Name des Prozesses
        /// Value = Liste von Schlüsselwörtern im Dateinamen zur Identifizierung
        /// </summary>
        public static readonly Dictionary<string, List<string>> EdiProcesses = new Dictionary<string, List<string>>
        {
            {"GPKE GeLi Gas", new List<string> {"GPKE GeLi Gas"}},
            {"MaBiS", new List<string> {"MaBiS"}},
            {"WiM", new List<string> {"WiM"}},
            {"Einspeiser", new List<string> {"Einspeiser"}},
            {"Netzbetreiberwechsel", new List<string> {"Netzbetreiberwechsel"}},
            {"Geschäftsdatenanfrage", new List<string> {"Geschäftsdatenanfrage"}},
            {"HKNR", new List<string> {"HKNR", "Herkunftsnachweisregister"}},
        };
    }
}
