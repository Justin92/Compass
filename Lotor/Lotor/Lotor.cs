﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using CSMSL;
using CSMSL.Chemistry;
using CSMSL.IO;
using CSMSL.IO.OMSSA;
using CSMSL.IO.Thermo;
using CSMSL.Proteomics;
using LumenWorks.Framework.IO.Csv;



namespace Coon.Compass.Lotor
{
    public class Lotor
    {
        private readonly Tolerance _prodTolerance;
        private readonly string _rawFileDirectory;
        private readonly string _csvFile;
        private readonly string _outputDirectory;
        public static List<IMass> FixedModifications;
        private readonly List<Modification> _fixedModifications;
        public static List<Modification> QuantifiedModifications; 
        private DateTime _startTime;
        //private readonly double _ascoreThreshold;
        private readonly int _deltaScoreCutoff;
        private readonly double _productThreshold;
        private readonly FragmentTypes _fragType;
        private readonly bool _ignoreCTerminal;
        private readonly bool _separateProteinGroups;
        private readonly bool _reduceSites;
        private readonly bool _phosphoNeutralLoss;
        private HashSet<IMSDataFile> _dataFiles; 

        private int FirstQuantColumn = -1;
        private int LastQuantColumn = -1;
        private string[] headerInfo = null;

        public Lotor(string rawFileDirectory, string inputcsvFile, string outputDirectory, List<Modification> fixedModifications, List<Modification> quantifiedModifications, Tolerance prod_Tolerance, int scoreCutoff, bool separateGroups, double productThreshold,bool ignoreCTerminal,bool reduceSites, FragmentTypes fragType, bool phosphoNeutralLoss = false)
        {
            _rawFileDirectory = rawFileDirectory;
            _csvFile = inputcsvFile;
            _outputDirectory = outputDirectory;
            _fixedModifications = fixedModifications;
            FixedModifications = fixedModifications.OfType<IMass>().ToList();
            QuantifiedModifications = quantifiedModifications;
            _prodTolerance = prod_Tolerance;            
            _deltaScoreCutoff = scoreCutoff;
            _separateProteinGroups = separateGroups;
            _productThreshold = productThreshold;
            _fragType = fragType;
            _ignoreCTerminal = ignoreCTerminal;
            _reduceSites = reduceSites;
            //LocalizedHit.AScoreThreshold = ascore_threshold;
            LocalizedHit.ScoreThreshold = scoreCutoff;
            _phosphoNeutralLoss = phosphoNeutralLoss;
        }

        public void Localize()
        {
            _startTime = DateTime.Now;
            Log("Localization Started...");

            Log("Ignore C Terminal Mods: " + _ignoreCTerminal);
            Log("Phospho Neutral Loss: " + _phosphoNeutralLoss);
            Log("Score Threshold: " + _deltaScoreCutoff);
            Log("Product Tolerance: " + _prodTolerance);
            Log("Product Threshold: " + (_productThreshold *100)+ "%");

            try
            {
                // 1) Read in all the psms and map them to their respective spectra
                List<PSM> psms = LoadAllPSMs(_csvFile, _rawFileDirectory, _fixedModifications);
                
                // 2) Calculate all the best isoforms for all the psms
                List<LocalizedHit> hits = CalculateBestIsoforms(psms, _fragType, _prodTolerance, _productThreshold, _phosphoNeutralLoss);

                // 3) Compile Results
                List<Protein> proteins = CompileResults(hits, _csvFile, _outputDirectory, _separateProteinGroups);

                if (_reduceSites)
                {
                    // 4) Write out the results
                    WriteResults(proteins, _csvFile, _outputDirectory, FirstQuantColumn, LastQuantColumn);
                }               
            }
            catch (Exception e)
            {                
                Log(e.Message, true);
            }
            finally
            {
                foreach (IMSDataFile dataFile in _dataFiles)
                {
                    dataFile.Dispose();
                }
                TimeSpan diff = DateTime.Now - _startTime;
                Log(string.Format("Finished [{0:D2} hrs, {1:D2} mins, {2:D2} secs]", diff.Hours, diff.Minutes, diff.Seconds));
                Log(string.Format("Lotor v{0}", lotorForm.GetRunningVersion()));
                OnFinished();                
            }
        }

        private void WriteResults(List<Protein> proteins, string csvFile, string outDirectory, int firstQuant, int lastQuant)
        {
            using (StreamWriter localizeWriter = new StreamWriter(Path.Combine(outDirectory, Path.GetFileNameWithoutExtension(csvFile) + "_localized_reduced.csv")))
            using (StreamWriter proteinWriter = new StreamWriter(Path.Combine(outDirectory, Path.GetFileNameWithoutExtension(csvFile) + "_localized_reduced_proteins.csv")))                
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("Protein Group,Defline,Isoform,Sites,PSMs Identified,PSMs Localized");
                if (firstQuant >= 0)
                {
                    for (int i = firstQuant; i <= lastQuant; i++)
                    {
                        sb.Append(',');
                        sb.Append(headerInfo[i]);
                    }
                }
                localizeWriter.WriteLine(sb.ToString());
                proteinWriter.WriteLine("Protein Group,Defline,Isoforms,# of Localized Isoforms,# of Localized PSMs");
                foreach (Protein prot in proteins)
                {
                    string value = prot.Print(firstQuant, lastQuant);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        localizeWriter.WriteLine(value);
                        proteinWriter.WriteLine(prot.GetProteinLine());
                    }
                }
            }
        }

        private List<Protein> CompileResults(List<LocalizedHit> hits, string csvFile, string outputDirectory, bool breakProteinsApart = false)
        {
            Dictionary<string, LocalizedHit> hitsdict = new Dictionary<string, LocalizedHit>();
 
            // Group all the localized Hits into proteins
            Dictionary<string, Protein> proteins = new Dictionary<string, Protein>();
            
            foreach (LocalizedHit hit in hits)
            {
                hitsdict.Add(hit.PSM.Filename, hit);
                
               
                string defline = hit.PSM.Defline;

                if (breakProteinsApart)
                {
                    string[] groups = hit.PSM.ProteinGroup.Split('|');
                    foreach (string group in groups)
                    {
                        Protein prot;
                        if (!proteins.TryGetValue(group, out prot))
                        {
                            prot = new Protein(group, defline);
                            proteins.Add(group, prot);
                        }
                        prot.AddHit(hit);
                    }
                }
                else
                {
                    Protein prot;
                    if (!proteins.TryGetValue(hit.PSM.ProteinGroup, out prot))
                    {
                        prot = new Protein(hit.PSM.ProteinGroup, defline);
                        proteins.Add(hit.PSM.ProteinGroup, prot);
                    }
                    prot.AddHit(hit);
                }
            }
            using (StreamWriter writer = new StreamWriter(Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(csvFile) + "_all.csv")),
                 localizedWriter = new StreamWriter(Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(csvFile) + "_localized.csv")))
            {
                using (CsvReader reader = new CsvReader(new StreamReader(csvFile), true))
                {
                    LocalizedHit hit = null;                   
                    headerInfo = reader.GetFieldHeaders();
                    bool tqFound = false;
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        if (headerInfo[i].EndsWith("NL)"))
                        {
                            if (!tqFound)
                            {
                                FirstQuantColumn = i;
                                tqFound = true;
                            }
                        }
                        if(headerInfo[i] == "Channels Detected")
                            LastQuantColumn = i-1;
                    }
                    string header = string.Join(",", headerInfo) + ",# Isoforms,# of Considered Fragments,Localized?,Delta Score,Best Isoform,Spectral Matches,% TIC,Second Best Isoform,Second Spectral Matches,Second % TIC";
                    writer.WriteLine(header);
                    localizedWriter.WriteLine(header);
                    while (reader.ReadNextRecord())
                    {
                        string mods = reader["Mods"];
                        if (string.IsNullOrEmpty(mods))
                            continue;

                        List<Modification> variableMods = OmssaModification.ParseModificationLine(mods).Select(item => item.Item1).OfType<Modification>().ToList();

                        // Only keep things with quantified Modifications
                        if (!variableMods.Any(mod => QuantifiedModifications.Contains(mod)))
                            continue;

                        string filename = reader["Filename/id"];
                        if(!hitsdict.TryGetValue(filename, out hit))
                            continue;
                     
                        string[] data = new string[reader.FieldCount];
                        reader.CopyCurrentRecordTo(data);
                  
                        hit.omssapsm = data;
                                 
                        StringBuilder sb = new StringBuilder();

                        foreach (string datum in data)
                        {
                            if (datum.Contains(','))
                            {
                                sb.Append("\"");
                                sb.Append(datum);
                                sb.Append("\"");
                            }
                            else
                                sb.Append(datum);
                            sb.Append(',');
                        }
                        sb.Append(hit.PSM.Isoforms);
                        sb.Append(',');
                        sb.Append(hit.LocalizedIsoform.Fragments.Count);
                        sb.Append(',');
                        sb.Append(hit.IsLocalized);
                        sb.Append(',');
                        sb.Append(hit.MatchDifference);
                        sb.Append(',');                       
                        sb.Append(hit.LocalizedIsoform.SequenceWithModifications);
                        sb.Append(',');
                        sb.Append(hit.LocalizedIsoform.SpectralMatch.Matches);
                        sb.Append(',');
                        sb.Append(hit.LocalizedIsoform.SpectralMatch.PercentTIC);
                        if (hit.PSM.Isoforms > 1)
                        {
                            //sb.Append(',');
                            //sb.Append(hit.BestPeptideSDFCount);
                            sb.Append(',');
                            sb.Append(hit.SecondBestPeptideIsoform.SequenceWithModifications);
                            sb.Append(',');
                            sb.Append(hit.SecondBestPeptideIsoform.SpectralMatch.Matches);
                            sb.Append(',');
                            sb.Append(hit.SecondBestPeptideIsoform.SpectralMatch.PercentTIC);
                            //sb.Append(',');
                            //sb.Append(hit.SecondBestPeptideSDFCount);
                        }

                        if(hit.IsLocalized)
                            localizedWriter.WriteLine(sb.ToString());
                        writer.WriteLine(sb.ToString());
                    }
                }
            }
      
            return proteins.Values.ToList();
        }

        private List<LocalizedHit> CalculateBestIsoforms(List<PSM> psms, FragmentTypes fragType, Tolerance prod_tolerance, double productThreshold, bool phosphoNeutralLosses)
        {
            Log("Localizing Best Isoforms...");
            int totalisofromscount = 0;
            int count = 0;
            int psm_count = 0;
            int localized_psm = 0;
            List<LocalizedHit> hits = new List<LocalizedHit>();          
            foreach (PSM psm in psms)
            {
                psm_count++;
                count++;

                // Generate all the isoforms for the PSM
                int isoformCount = psm.GenerateIsoforms(_ignoreCTerminal);

                if(isoformCount == 0)
                    continue;

                totalisofromscount += isoformCount;
                
                // Calculate the probability of success for random matches
                //double pvalue = GetPValue(psm, prod_tolerance, productThreshold);

                // Match all the isoforms to the spectrum and log the results
                psm.MatchIsofroms(fragType, prod_tolerance, productThreshold, phosphoNeutralLosses, 1);

                // Perform the localization for all combinations of isoforms
                //double[,] res = psm.Calc(pvalue);

                Tuple<int, int, double> scores = LocalizedIsoformSimple(psm);

                // Check if the localization is above some threshold               
                //Tuple<int, int, double> scores = LocalizedIsoform(res);
                int bestIsoform = scores.Item1;
                int secondBestIsoform = scores.Item2;
                double ascore = scores.Item3;
                //int numSDFs = psm.NumSiteDeterminingFragments[bestIsoform, secondBestIsoform];
                          
                List<PeptideIsoform> isoforms = psm.PeptideIsoforms.ToList();
                //LocalizedHit hit = new LocalizedHit(psm, isoforms[bestIsoform], isoforms[secondBestIsoform], numSDFs,
                //    psm.BestSiteDeterminingFragments[bestIsoform, secondBestIsoform],
                //    psm.BestSiteDeterminingFragments[secondBestIsoform, bestIsoform], pvalue, ascore);

                LocalizedHit hit = new LocalizedHit(psm, isoforms[bestIsoform], isoforms[secondBestIsoform], 0,
                   0,
                   0, 0, ascore);
                hits.Add(hit);
                if (hit.IsLocalized)
                {
                    localized_psm++;
                }

                psm.PeptideIsoforms.Clear();

                // Progress Bar Stuff
                if (count <= 50) 
                    continue;
                count = 0;
                ProgressUpdate((double)psm_count / psms.Count);
            }
            ProgressUpdate(1.0);
            Log(string.Format("Total Number of Possible Isoforms Considered: {0:N0}",totalisofromscount));
            Log(string.Format("Total Number of PSMs Considered: {0:N0}", psm_count));
            Log(string.Format("Total Number of PSMs Localized: {0:N0} ({1:00.00}%)", localized_psm, localized_psm * 100 / psm_count));       
            ProgressUpdate(0);
            return hits;
        }

        private static Tuple<int, int, double> LocalizedIsoformSimple(PSM psm)
        {
            int length = psm.Isoforms;
            List<PeptideIsoform> isoforms = psm.PeptideIsoforms.ToList();

            if (length == 1)
                return new Tuple<int, int, double>(0, 0, double.PositiveInfinity);

            int biggesti = 0;
            int bestI = 0;

            for (int i = 0; i < length; i++)
            {
                var isoform = isoforms[i];
                if (isoform.SpectralMatch.Matches > biggesti)
                {
                    biggesti = isoform.SpectralMatch.Matches;
                    bestI = i;
                }
            }

            int biggestj = 0;
            int bestJ = bestI;

            for (int j = 0; j < length; j++)
            {
                if (j == bestI)
                    continue;
                
                var isoform = isoforms[j];
                if (isoform.SpectralMatch.Matches > biggestj)
                {
                    biggestj = isoform.SpectralMatch.Matches;
                    bestJ = j;
                }
            }

            return new Tuple<int, int, double>(bestI, bestJ, double.PositiveInfinity);
        }
        
        private List<PSM> LoadAllPSMs(string csvFile, string rawFileDirectory, List<Modification> fixedMods)
        {
            ProgressUpdate(0.0); //force the progressbar to go into marquee mode  
            Log("Reading PSMs from " + csvFile);

            Dictionary<string, ThermoRawFile> rawFiles =
                Directory.EnumerateFiles(rawFileDirectory, "*.raw", SearchOption.AllDirectories)
                    .ToDictionary(Path.GetFileNameWithoutExtension, file => new ThermoRawFile(file));

            _dataFiles = new HashSet<IMSDataFile>();

            List<PSM> psms = new List<PSM>();

            int totalPsms = 0;

            using (CsvReader reader = new CsvReader(new StreamReader(csvFile), true))
            {
                while (reader.ReadNextRecord())
                {
                    string mods = reader["Mods"];

                    totalPsms++;

                    // Skip if there are no modifications
                    if (string.IsNullOrEmpty(mods))
                        continue;

                    // Convert the text mod line into a list of modification objects
                    List<Modification> variableMods = OmssaModification.ParseModificationLine(mods).Select(item => item.Item1).ToList();

                    // Only keep things with quantified Modifications
                    if (!variableMods.Any(mod => QuantifiedModifications.Contains(mod)))
                        continue;

                    string filename = reader["Filename/id"];
                    string rawname = filename.Split('.')[0];

                    int scanNumber = int.Parse(reader["Spectrum number"]);

                    PSM psm = new PSM(scanNumber, rawname);
                    psm.StartResidue = int.Parse(reader["Start"]);
                    psm.Charge = int.Parse(reader["Charge"]);
                    psm.BasePeptide = new Peptide(reader["Peptide"].ToUpper());
                    psm.Defline = reader["Defline"];
                    psm.ProteinGroup = reader["Best PG Name"];
                    psm.NumberOfSharingProteinGroups = int.Parse(reader["# of Sharing PGs"]);
                    psm.Filename = filename;

                    // Apply all the fix modifications
                    psm.BasePeptide.SetModifications(fixedMods);

                    int i = 0;
                    while (i < variableMods.Count)
                    {
                        if (fixedMods.Contains(variableMods[i]))
                        {
                            variableMods.RemoveAt(i);
                        }
                        else
                        {
                            i++;
                        }
                    }

                    // Save all the variable mod types             
                    psm.VariabledModifications = variableMods;

                    psms.Add(psm);
                }
            }

            Log(string.Format("{0:N0} PSMs were loaded....", totalPsms));
            Log(string.Format("{0:N0} PSMs were kept.... ({1:F2} %)", psms.Count, 100.0 * (double)psms.Count / totalPsms));

            Log("Reading Spectral Data...");
            ThermoRawFile currentRawFile = null;
            string currentRawFileName = null;
            int counter = 0;

            foreach (PSM psm in psms.OrderBy(psm => psm.RawFileName))
            {
                string rawfilename = psm.RawFileName;

                if(!rawfilename.Equals(currentRawFileName)) {
                    currentRawFileName = rawfilename;
                    if (currentRawFile != null && currentRawFile.IsOpen)
                        currentRawFile.Dispose();

                    if (!rawFiles.TryGetValue(rawfilename, out currentRawFile))
                    {
                        throw new NullReferenceException(string.Format("Raw File: {0}.raw was not found! Aborting.", rawfilename));
                    }
                    currentRawFile.Open();
                }

                psm.SetRawFile(currentRawFile);
                counter++;
                if(counter % 25 == 0)
                {
                    ProgressUpdate((double)counter / psms.Count);
                }
            }       

            return psms;
        }
             
        #region Statics
        
        public static Tuple<int,int, double> LocalizedIsoform(double[,] data)
        {
            double lowestAscore = 0;
            int length = data.GetLength(0);

            if (length == 1)
                return new Tuple<int, int, double>(0, 0, double.PositiveInfinity);

            int bestIsoform = 0;
            int secondBestIsoform = 0;
            int bestJ = 0;
            for (int i = 0; i < length; i++)
            {
                // Find the minimum value >= 0 in the column (i)
                double minvalue = double.MaxValue;
                for (int j = 0; j < length; j++)
                {
                    if (i == j) continue;

                    double value = data[i, j];

                    if (value == 0.0)
                    {
                        minvalue = 0;
                        break;
                    }

                    if (value < 0)
                    {
                        // Contains a negative value, meaning some isoforms beats this one (i) so just skip this loop entirely
                        minvalue = -1;
                        break;
                    }

                    if (value <= minvalue) 
                    {
                        minvalue = value;
                        bestJ = j;
                    }
                }

                // If this is the global minimum, save both isoforms (i,j) and the score
                if (minvalue >= lowestAscore)
                {
                    lowestAscore = minvalue;
                    bestIsoform = i;
                    secondBestIsoform = bestJ;
                }
            }
           
            return new Tuple<int, int, double>(bestIsoform,secondBestIsoform,lowestAscore);
        }
        
        #endregion

        #region CallBacks

        public event EventHandler<StatusEventArgs> UpdateLog;
        public void Log(string message, bool isError = false)
        {
            var handler = UpdateLog;
            if (handler != null)
            {
                handler(this, new StatusEventArgs(message, isError));
            }          
        }

        public event EventHandler<ProgressEventArgs> UpdateProgress;
        public void ProgressUpdate(double percent)
        {
            var handler = UpdateProgress;
            if (handler != null)
            {
                handler(this, new ProgressEventArgs(percent));
            }           
        }

        public event EventHandler Completed;
        protected virtual void OnFinished()
        {
            var handler = Completed;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        #endregion

    }
}
