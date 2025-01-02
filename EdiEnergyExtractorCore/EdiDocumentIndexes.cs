using System.Linq;
using EdiEnergyExtractorCore;
using Raven.Client.Documents.Indexes;

namespace EdiEnergyExtractor;

class EdiDocuments_DocumentUri : AbstractIndexCreationTask<EdiDocument>
{
    public EdiDocuments_DocumentUri()
    {
        Map = ediDocs => from ediDoc in ediDocs
                         select new { ediDoc.DocumentUri };
    }
}
