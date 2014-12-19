﻿using System;
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

            const string edienergyJsonFile = "EdiEnergy.json";
            WriteFileIfChanged(edienergyJsonFile, json);

            Console.WriteLine("+++Done+++");
            Console.ReadLine();
        }

        private static void WriteFileIfChanged(string edienergyJsonFile, string json)
        {
            if (!File.Exists(edienergyJsonFile) || json != File.ReadAllText(edienergyJsonFile))
            {
                Console.WriteLine("Writing file: {0}", edienergyJsonFile);
                File.WriteAllText(edienergyJsonFile, json);
            }
            else
            {
                Console.WriteLine("Skipping file write, because nothing changed! ({0})", edienergyJsonFile);
                
            }
        }
    }
}
