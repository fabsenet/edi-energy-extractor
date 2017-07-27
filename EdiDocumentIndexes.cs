using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Raven.Client.Documents.Indexes;

namespace Fabsenet.EdiEnergy
{
    class EdiDocuments_MirrorUri : AbstractIndexCreationTask<EdiDocument>
    {
        public EdiDocuments_MirrorUri()
        {
            Map = ediDocs => from ediDoc in ediDocs
                             select new { ediDoc.MirrorUri };
        }
    }

    class EdiDocuments_DocumentUri : AbstractIndexCreationTask<EdiDocument>
    {
        public EdiDocuments_DocumentUri()
        {
            Map = ediDocs => from ediDoc in ediDocs
                             select new { ediDoc.DocumentUri };
        }
    }
}
