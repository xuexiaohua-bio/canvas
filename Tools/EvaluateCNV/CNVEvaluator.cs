using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CanvasCommon;
using Illumina.Common;

namespace EvaluateCNV
{
    internal class BaseCounter
    {
        public int TotalVariants { get; set; }
        public int TotalVariantBases { get; set; }
        public double MeanAccuracy { get; set; }
        public double MedianAccuracy { get; set; }
        public int MinSize { get; }
        public int MaxSize { get; }
        public long[,] BaseCount;
        public long[,] RoiBaseCount;


        public BaseCounter(int maxCN, int minSize, int maxSize, bool hasRoi = false)
        {
            MinSize = minSize;
            MaxSize = maxSize;
            BaseCount = new long[maxCN + 1, maxCN + 1];
            if (hasRoi)
                RoiBaseCount = new long[maxCN + 1, maxCN + 1];
        }
    }

    internal class VcfHeaderInfo
    {
        public double? Ploidy { get; }
        public double? Purity { get; }

        public VcfHeaderInfo(double? ploidy, double? purity)
        {
            Ploidy = ploidy;
            Purity = purity;
        }
    }

    class CNVEvaluator
    {
        private CNVChecker _cnvChecker;
        #region Members
        static int maxCN = 10;
        #endregion

        public CNVEvaluator(CNVChecker cnvChecker)
        {
            _cnvChecker = cnvChecker;
        }

        public void ComputeAccuracy(string truthSetPath, string cnvCallsPath, string outputPath, PloidyInfo ploidyInfo, bool includePassingOnly,
            EvaluateCnvOptions options)
        {
            // Make a note of how many bases in the truth set are not *actually* considered to be known bases, using
            // the "cnaqc" exclusion set:
            _cnvChecker.InitializeIntervalMetrics();
            bool regionsOfInterest = _cnvChecker.RegionsOfInterest != null;
            var baseCounters = new List<BaseCounter> { new BaseCounter(maxCN, 0, Int32.MaxValue, regionsOfInterest) };
            if (options.SplitBySize)
            {
                baseCounters.Add(new BaseCounter(maxCN, 0, 4999, regionsOfInterest));
                baseCounters.Add(new BaseCounter(maxCN, 5000, 9999, regionsOfInterest));
                baseCounters.Add(new BaseCounter(maxCN, 10000, 99999, regionsOfInterest));
                baseCounters.Add(new BaseCounter(maxCN, 100000, 499999, regionsOfInterest));
                baseCounters.Add(new BaseCounter(maxCN, 500000, int.MaxValue, regionsOfInterest));
            }

            _cnvChecker.CountExcludedBasesInTruthSetIntervals();
            if (_cnvChecker.DQscoreThreshold.HasValue && !Path.GetFileName(cnvCallsPath).ToLower().Contains("vcf"))
                throw new ArgumentException("CNV.vcf must be in a vcf format when --dqscore option is used");
            var calls = _cnvChecker.GetCnvCallsFromVcf(cnvCallsPath, includePassingOnly);
            var headerInfo = _cnvChecker.GetVCFHeaderInfo(cnvCallsPath);

            foreach (var baseCounter in baseCounters)
            {
                CalculateMetrics(ploidyInfo, calls, baseCounter, options.SkipDiploid);
                string fileName = $"{options.BaseFileName}";
                if (options.DQscoreThreshold.HasValue)
                {
                    fileName += "_denovo";
                }
                if (baseCounter.MinSize != 0 || baseCounter.MaxSize != int.MaxValue)
                {
                    fileName += $"_{Math.Round(baseCounter.MinSize / 1000.0)}kb";
                    fileName += baseCounter.MaxSize == int.MaxValue ? "+" : $"_{ Math.Round(baseCounter.MaxSize / 1000.0)}kb";
                }
                fileName += ".txt";
                using (FileStream stream = new FileStream(Path.Combine(outputPath, fileName), includePassingOnly ?
                FileMode.Create : FileMode.Append, FileAccess.Write))
                using (StreamWriter outputWriter = new StreamWriter(stream))
                {
                    WriteResults(headerInfo, truthSetPath, cnvCallsPath, outputWriter, baseCounter, includePassingOnly);
                }
            }
        }

        private void CalculateMetrics(PloidyInfo ploidyInfo, IEnumerable<CNVCall> calls, BaseCounter baseCounter, bool optionsSkipDiploid)
        {
            ploidyInfo.MakeChromsomeNameAgnosticWithAllChromosomes(calls.Select(call => call.Chr));
            foreach (CNVCall call in calls)
            {
                int CN = call.CN;
                if (CN < 0 || call.End < 0) continue; // Not a CNV call, apparently
                if (call.AltAllele == "." && optionsSkipDiploid) continue;
                if (!(call.Length >= baseCounter.MinSize && call.Length <= baseCounter.MaxSize)) continue;

                int basesOverlappingPloidyRegion = 0;
                int variantBasesOverlappingPloidyRegion = 0;
                foreach (var ploidyInterval in ploidyInfo.PloidyByChromosome[call.Chr])
                {
                    int overlap = call.Overlap(ploidyInterval);
                    basesOverlappingPloidyRegion += overlap;
                    if (CN != ploidyInterval.Ploidy)
                        variantBasesOverlappingPloidyRegion += overlap;
                }
                baseCounter.TotalVariantBases += variantBasesOverlappingPloidyRegion;
                if (CN != 2)
                {
                    baseCounter.TotalVariantBases += call.Length - basesOverlappingPloidyRegion;
                }
                if (variantBasesOverlappingPloidyRegion > 0 || CN != 2 && variantBasesOverlappingPloidyRegion < call.Length)
                {
                    baseCounter.TotalVariants++;
                }

                if (CN > maxCN) CN = maxCN;
                string chr = call.Chr;
                if (!_cnvChecker.KnownCN.ContainsKey(chr)) chr = call.Chr.Replace("chr", "");
                if (!_cnvChecker.KnownCN.ContainsKey(chr)) chr = "chr" + call.Chr;
                if (!_cnvChecker.KnownCN.ContainsKey(chr))
                {
                    Console.WriteLine("Error: Skipping variant call for chromosome {0} with no truth data", call.Chr);
                    continue;
                }
                foreach (CNInterval interval in _cnvChecker.KnownCN[chr])
                {
                    int overlapStart = Math.Max(call.Start, interval.Start);
                    int overlapEnd = Math.Min(call.End, interval.End);
                    if (overlapStart >= overlapEnd) continue;
                    int overlapBases = overlapEnd - overlapStart;
                    // We've got an overlap interval.  Kill off some bases from this interval, if it happens
                    // to overlap with an excluded interval:
                    if (_cnvChecker.ExcludeIntervals.ContainsKey(chr))
                    {
                        foreach (CNInterval excludeInterval in _cnvChecker.ExcludeIntervals[chr])
                        {
                            int excludeOverlapStart = Math.Max(excludeInterval.Start, overlapStart);
                            int excludeOverlapEnd = Math.Min(excludeInterval.End, overlapEnd);
                            if (excludeOverlapStart >= excludeOverlapEnd) continue;
                            overlapBases -= (excludeOverlapEnd - excludeOverlapStart);
                        }
                    }

                    int knownCN = interval.CN;
                    if (knownCN > maxCN) knownCN = maxCN;
                    baseCounter.BaseCount[knownCN, CN] += overlapBases;

                    interval.BasesCovered += overlapBases;
                    if (knownCN == CN)
                    {
                        interval.BasesCalledCorrectly += overlapBases;
                    }
                    else
                    {
                        interval.BasesCalledIncorrectly += overlapBases;
                    }

                    if (_cnvChecker.RegionsOfInterest != null && _cnvChecker.RegionsOfInterest.ContainsKey(chr))
                    {
                        foreach (CNInterval roiInterval in _cnvChecker.RegionsOfInterest[chr])
                        {
                            int roiOverlapStart = Math.Max(roiInterval.Start, overlapStart);
                            int roiOverlapEnd = Math.Min(roiInterval.End, overlapEnd);
                            if (roiOverlapStart >= roiOverlapEnd) continue;
                            int roiOverlapBases = roiOverlapEnd - roiOverlapStart;
                            baseCounter.RoiBaseCount[knownCN, CN] += roiOverlapBases;
                        }
                    }
                }
            }

            CalculateMedianAndMeanAccuracies(baseCounter);

            var allIntervals = _cnvChecker.KnownCN.SelectMany(kvp => kvp.Value);

            // find truth interval with highest number of false negatives (hurts recall)
            var variantIntervals = allIntervals.Where(interval => interval.CN != interval.ReferenceCopyNumber);
            var intervalMaxFalseNegatives = variantIntervals.MaxBy(interval => interval.BasesNotCalled + interval.BasesCalledIncorrectly);
            Console.WriteLine($"Truth interval with most false negatives (hurts recall): {intervalMaxFalseNegatives}");

            // find truth interval with highest number of false positive (hurts precision)
            var refIntervals = allIntervals.Where(interval => interval.CN == interval.ReferenceCopyNumber);
            var intervalMaxFalsePositives = refIntervals.MaxBy(interval => interval.BasesCalledIncorrectly);
            Console.WriteLine($"Truth interval with most false positives (hurts precision): {intervalMaxFalsePositives}");
        }

        /// <summary>
        /// For each CNV calls in the truth set, compute the fraction of bases assigned correct copy number
        /// </summary>
        /// <param name="baseCounter"></param>
        private void CalculateMedianAndMeanAccuracies(BaseCounter baseCounter)
        {
            baseCounter.MeanAccuracy = 0;
            baseCounter.MedianAccuracy = 0;
            var eventAccuracies = new List<double>();
            foreach (string chr in _cnvChecker.KnownCN.Keys)
            {
                foreach (var interval in _cnvChecker.KnownCN[chr])
                {
                    if (interval.CN == 2) continue;
                    int basecount = interval.Length - interval.BasesExcluded;
                    if (basecount <= 0) continue;
                    double accuracy = interval.BasesCalledCorrectly / (double)basecount;
                    eventAccuracies.Add(accuracy);
                    baseCounter.MeanAccuracy += accuracy;
                    //Console.WriteLine("{0}\t{1:F4}", interval.End - interval.Start, accuracy);
                }
            }
            eventAccuracies.Sort();
            baseCounter.MeanAccuracy /= Math.Max(1, eventAccuracies.Count);
            baseCounter.MedianAccuracy = double.NaN;
            if (eventAccuracies.Count > 0)
                baseCounter.MedianAccuracy = eventAccuracies[eventAccuracies.Count / 2];
            Console.WriteLine($"Event-level accuracy mean {baseCounter.MeanAccuracy:F4} median {baseCounter.MedianAccuracy:F4}" +
                              $" for variants sizes {baseCounter.MinSize} to {baseCounter.MaxSize}");
        }

        static void WriteResults(VcfHeaderInfo headerInfo, string truthSetPath, string cnvCallsPath, StreamWriter outputWriter, BaseCounter baseCounter, bool includePassingOnly)
        {
            // Compute overall stats:
            long totalBases = 0;
            long totalBasesRight = 0;
            long totalBasesRightDirection = 0;

            long isGainBases = 0;
            long callGainBases = 0;
            long isGainBasesCorrect = 0;
            long isGainBasesCorrectDirection = 0;

            long isLossBases = 0;
            long callLossBases = 0;
            long isLossBasesCorrect = 0;
            long isLossBasesCorrectDirection = 0;
            for (int trueCN = 0; trueCN <= maxCN; trueCN++)
            {
                for (int callCN = 0; callCN <= maxCN; callCN++)
                {
                    long bases = baseCounter.BaseCount[trueCN, callCN];
                    totalBases += bases;
                    if (trueCN == callCN) totalBasesRight += bases;
                    if (trueCN < 2 && callCN < 2 || trueCN == 2 && callCN == 2 || trueCN > 2 && callCN > 2)
                        totalBasesRightDirection += bases;
                    if (trueCN < 2) isLossBases += bases;
                    if (trueCN > 2) isGainBases += bases;
                    if (callCN < 2) callLossBases += bases;
                    if (callCN > 2) callGainBases += bases;
                    if (trueCN == callCN && trueCN < 2) isLossBasesCorrect += bases;
                    if (trueCN == callCN && trueCN > 2) isGainBasesCorrect += bases;
                    if (trueCN > 2 && callCN > 2) isGainBasesCorrectDirection += bases;
                    if (trueCN < 2 && callCN < 2) isLossBasesCorrectDirection += bases;
                }
            }

            // Compute ROI stats:
            long ROIBases = 0;
            long ROIBasesCorrect = 0;
            long ROIBasesCorrectDirection = 0;
            if (baseCounter.RoiBaseCount != null)
            {
                for (int trueCN = 0; trueCN <= maxCN; trueCN++)
                {
                    for (int callCN = 0; callCN <= maxCN; callCN++)
                    {
                        long bases = baseCounter.RoiBaseCount[trueCN, callCN];
                        ROIBases += bases;
                        if (trueCN == callCN) ROIBasesCorrect += bases;
                        if (trueCN < 2 && callCN < 2 || trueCN == 2 && callCN == 2 || trueCN > 2 && callCN > 2)
                            ROIBasesCorrectDirection += bases;
                    }
                }
            }

            // Report stats:
            string purity = headerInfo.Purity.HasValue ? headerInfo.Purity.Value.ToString(CultureInfo.InvariantCulture) : "NA";
            outputWriter.WriteLine($"Purity\t{purity}");
            string ploidy = headerInfo.Ploidy.HasValue ? headerInfo.Ploidy.Value.ToString(CultureInfo.InvariantCulture) : "NA";
            outputWriter.WriteLine($"Ploidy\t{ploidy}");
            outputWriter.WriteLine(includePassingOnly ? "Results for PASSing variants" : "Results for all variants");
            outputWriter.WriteLine("TruthSet\t{0}", truthSetPath);
            outputWriter.WriteLine("CNVCalls\t{0}", cnvCallsPath);
            outputWriter.WriteLine("Accuracy\t{0:F4}", 100 * totalBasesRight / (double)totalBases);
            outputWriter.WriteLine("DirectionAccuracy\t{0:F4}", 100 * totalBasesRightDirection / (double)totalBases);
            outputWriter.WriteLine("Recall\t{0:F4}",
                100 * (isGainBasesCorrect + isLossBasesCorrect) / (double)(isGainBases + isLossBases));
            outputWriter.WriteLine("DirectionRecall\t{0:F4}",
                100 * (isGainBasesCorrectDirection + isLossBasesCorrectDirection) / (double)(isGainBases + isLossBases));
            outputWriter.WriteLine("Precision\t{0:F4}",
                100 * (isGainBasesCorrect + isLossBasesCorrect) / (double)(callGainBases + callLossBases));
            outputWriter.WriteLine("DirectionPrecision\t{0:F4}",
                100 * (isGainBasesCorrectDirection + isLossBasesCorrectDirection) / (double)(callGainBases + callLossBases));
            outputWriter.WriteLine("GainRecall\t{0:F4}", 100 * (isGainBasesCorrect) / (double)(isGainBases));
            outputWriter.WriteLine("GainDirectionRecall\t{0:F4}", 100 * (isGainBasesCorrectDirection) / (double)(isGainBases));
            outputWriter.WriteLine("GainPrecision\t{0:F4}", 100 * (isGainBasesCorrect) / (double)(callGainBases));
            outputWriter.WriteLine("GainDirectionPrecision\t{0:F4}",
                100 * (isGainBasesCorrectDirection) / (double)(callGainBases));
            outputWriter.WriteLine("LossRecall\t{0:F4}", 100 * (isLossBasesCorrect) / (double)(isLossBases));
            outputWriter.WriteLine("LossDirectionRecall\t{0:F4}", 100 * (isLossBasesCorrectDirection) / (double)(isLossBases));
            outputWriter.WriteLine("LossPrecision\t{0:F4}", 100 * (isLossBasesCorrect) / (double)(callLossBases));
            outputWriter.WriteLine("LossDirectionPrecision\t{0:F4}",
                100 * (isLossBasesCorrectDirection) / (double)(callLossBases));
            outputWriter.WriteLine("MeanEventAccuracy\t{0:F4}", 100 * baseCounter.MeanAccuracy);
            outputWriter.WriteLine("MedianEventAccuracy\t{0:F4}", 100 * baseCounter.MedianAccuracy);
            outputWriter.WriteLine("VariantEventsCalled\t{0}", baseCounter.TotalVariants);
            outputWriter.WriteLine("VariantBasesCalled\t{0}", baseCounter.TotalVariantBases);
            if (baseCounter.RoiBaseCount != null && ROIBases > 0)
            {
                outputWriter.WriteLine("ROIAccuracy\t{0:F4}", 100 * ROIBasesCorrect / (double)ROIBases);
                outputWriter.WriteLine("ROIDirectionAccuracy\t{0:F4}", 100 * ROIBasesCorrectDirection / (double)ROIBases);
            }
            // to separate passing and all variant results
            outputWriter.WriteLine();
        }
    }
}