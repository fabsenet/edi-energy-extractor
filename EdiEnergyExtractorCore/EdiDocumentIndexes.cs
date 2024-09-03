using System.Linq;
using Raven.Client.Documents.Indexes;

namespace EdiEnergyExtractorCore;

class EdiDocuments_DocumentUri : AbstractIndexCreationTask<EdiDocument>
{
    public EdiDocuments_DocumentUri()
    {
        Map = ediDocs => from ediDoc in ediDocs
                         select new { ediDoc.DocumentUri };
    }
}
