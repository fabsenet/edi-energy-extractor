﻿using System.Collections.Generic;

namespace EdiEnergyExtractor;

public static class EdiConstants
{
    public static readonly string[] MessageTypes =
    {
        "APERAK", "CONTRL", "IFTSTA", "INSRPT", "INVOIC", "MSCONS", "ORDERS", "ORDRSP", "PRICAT", "QUOTES", "REMADV", "REQOTE", "UTILMD", "COMDIS", "UTILTS", "PARTIN", "ORDCHG"
    };

    /// <summary>
    /// Key = Name des Prozesses
    /// Value = Liste von Schlüsselwörtern im Dateinamen zur Identifizierung
    /// </summary>
    public static readonly Dictionary<string, List<string>> EdiProcesses = new()
    {
        {"GPKE GeLi Gas", new List<string> {"GPKE GeLi Gas"}},
        {"MaBiS", new List<string> {"MaBiS"}},
        {"MaLo", new List<string> {"MaLo"}},
        {"WiM", new List<string> {"WiM"}},
        {"Einspeiser", new List<string> {"Einspeiser"}},
        {"Netzbetreiberwechsel", new List<string> {"Netzbetreiberwechsel"}},
        {"Geschäftsdatenanfrage", new List<string> {"Geschäftsdatenanfrage", "Gesch&auml;ftsdatenanfrage"}},
        {"HKNR", new List<string> {"HKNR", "Herkunftsnachweisregister"}},
        {"Stammdatenänderung", new List<string> {"Stammdatenänderung"}},
        {"Zählzeitdefinitionen", new List<string> {"Zählzeitdefinitionen"}},
        {"Berechnungsformel", new List<string> {"Berechnungsformel"}},
    };
}
