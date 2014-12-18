using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EdiEnergyExtractor
{
    class Program
    {
        static void Main(string[] args)
        {
            var dataExtractor = new DataExtractor();
            if (args.Any())
            {
                dataExtractor.LoadFromFile(args[0]);
            }
            else
            {
                //request data from actual web page
                dataExtractor.LoadFromWeb();
            }

            dataExtractor.AnalyzeResult();

            var json = dataExtractor.GetResultAsJson();
            Console.WriteLine(json);

            File.WriteAllText("EdiEnergy.json", json);

            Console.WriteLine("+++Done+++");
            Console.ReadLine();
        }
    }
}
