using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Newtonsoft.Json;

namespace Fabsenet.EdiEnergy
{
    public class EdiDocument
    {
        private string[] _containedMessageTypes;

        [UsedImplicitly]
        [JsonIgnore][Raven.Imports.Newtonsoft.Json.JsonIgnore]
        public bool IsCurrent { get; set; }

        [UsedImplicitly]
        public string DocumentName
        {
            get
            {
                return _documentNameRaw.Split('\n','\r').First();
            }
        }

        [UsedImplicitly]
        public string Id
        {
            get
            {
                if (_id == null)
                {
                    _id = string.Format("EdiDocuments/{0}", Path.GetFileNameWithoutExtension(DocumentUri.AbsolutePath));
                }
                return _id;
            }
            set { _id = value; }
        }

        [UsedImplicitly]
        public Uri DocumentUri { get; set; }
        [UsedImplicitly]
        public DateTime? ValidFrom { get; set; }
        [UsedImplicitly]
        public DateTime? ValidTo { get; set; }

        [UsedImplicitly]
        public bool IsMig
        {
            get { return DocumentNameRaw.Contains("MIG"); }
        }

        [UsedImplicitly]
        public bool IsAhb
        {
            get { return DocumentNameRaw.Contains("AHB") || BdewProcess!=null; }
        }

        [UsedImplicitly]
        public string[] ContainedMessageTypes
        {
            get { return _containedMessageTypes; }
        }

        [UsedImplicitly]
        public bool IsGeneralDocument
        {
            get { return !IsMig && !IsAhb; }
        }

        [UsedImplicitly]
        public string MessageTypeVersion
        {
            get
            {
                if (IsGeneralDocument) return null;

                var regex = new Regex(@"(\d\.\d[a-z]{0,1})");
                var match = regex.Match(DocumentNameRaw);
                if (!match.Success)
                {
                    return null;
                }

                return match.Groups[1].Value;
            }
        }

        private readonly CultureInfo _germanCulture = new CultureInfo("de-DE");
        private string _documentNameRaw;
        private string _id;

        [UsedImplicitly]
        public DateTime DocumentDate
        {
            get
            {
                if (DocumentNameRaw.Contains("Stand:"))
                {
                    //"UTILMD AHB GPKE GeLi Gas 6.0a\r\n Konsolidierte Lesefassung mit Fehlerkorrekturen\r\n Stand: 29. August 2014"
                    var dateString = DocumentNameRaw.Split(new[] {"Stand:"}, StringSplitOptions.None)[1].Trim();

                    var date = DateTime.Parse(dateString, _germanCulture);

                    return date;
                }

                //alternatively try parsing the date from the file name
                var filename = Path.GetFileNameWithoutExtension(DocumentUri.AbsoluteUri);

                //could be like "APERAK_MIG_2_1a_2014_04_01"
                if (Regex.IsMatch(filename, @"_\d{4}_\d{2}_\d{2}$"))
                {
                    var date = DateTime.ParseExact(filename.Substring(filename.Length - 10), "yyyy_MM_dd", _germanCulture);
                    return date;
                }

                //could be like "CONTRL-APERAK_AHB_2_3a_20141001"
                if (Regex.IsMatch(filename, @"_\d{8}$"))
                {
                    var date = DateTime.ParseExact(filename.Substring(filename.Length - 8), "yyyyMMdd", _germanCulture);
                    return date;
                }

                throw new Exception("Could not guess the document date for '"+DocumentUri+"'");
            }
        }

        [UsedImplicitly]
        public string BdewProcess
        {
            get
            {
                if (IsMig) return null;
                return EdiConstants.EdiProcesses.SingleOrDefault(p => DocumentNameRaw.Contains(p));
            }
        }

        [UsedImplicitly]
        [JsonIgnore]
        public string DocumentNameRaw
        {
            get { return _documentNameRaw; }
            set
            {
                _documentNameRaw = Regex.Replace(value, "[ ]+", " ");


                var containedMessageTypes = EdiConstants.MessageTypes.Where(mt => DocumentNameRaw.Contains(mt)).ToArray();
                _containedMessageTypes = containedMessageTypes.Any() ? containedMessageTypes : null;
            }
        }
    }
}