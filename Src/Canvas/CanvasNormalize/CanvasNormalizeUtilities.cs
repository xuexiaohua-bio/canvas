﻿using System;
using System.Collections.Generic;
using System.IO;
using CanvasCommon;
using Isas.SequencingFiles;
using Isas.Shared.Utilities.FileSystem;

namespace CanvasNormalize
{
    public class CanvasNormalizeUtilities
    {
        public static readonly double CanvasDiploidBinRatioFactor = 40;

        public static int GetPloidy(PloidyInfo referencePloidy, string chrom, int start, int end, int defaultPloidy = 2)
        {
            if (referencePloidy == null) { return defaultPloidy; }

            CanvasSegment segment = new CanvasSegment(chrom, start, end, new List<float>());

            return referencePloidy.GetReferenceCopyNumber(segment);
        }

        private static IEnumerable<GenomicBin> RatiosToCounts(IEnumerable<GenomicBin> ratios, PloidyInfo referencePloidy)
        {
            foreach (GenomicBin ratio in ratios)
            {
                // get the normal ploidy
                double factor = CanvasDiploidBinRatioFactor * GetPloidy(referencePloidy, ratio.Chromosome, ratio.Start, ratio.Stop) / 2.0;
                double count = ratio.Count * factor;
                yield return new GenomicBin(ratio.Chromosome, ratio.Start, ratio.Stop, ratio.GC, (float)count);
            }
        }

        public static void RatiosToCounts(IEnumerable<GenomicBin> ratios, IFileLocation referencePloidyBedFile,
            IFileLocation outputPath)
        {
            PloidyInfo referencePloidy = null;
            if (referencePloidyBedFile != null && referencePloidyBedFile.Exists)
                referencePloidy = PloidyInfo.LoadPloidyFromBedFile(referencePloidyBedFile.FullName);

            CanvasIO.WriteToTextFile(outputPath.FullName, RatiosToCounts(ratios, referencePloidy));
        }

        /// <summary>
        /// Writes copy-number data (cnd) file.
        /// </summary>
        /// <param name="fragmentCountFile"></param>
        /// <param name="referenceCountFile"></param>
        /// <param name="ratios"></param>
        /// <param name="outputFile"></param>
        public static void WriteCndFile(IFileLocation fragmentCountFile, IFileLocation referenceCountFile,
            IEnumerable<GenomicBin> ratios, IFileLocation outputFile)
        {
            IEnumerable<GenomicBin> fragmentCounts = CanvasIO.IterateThroughTextFile(fragmentCountFile.FullName);
            IEnumerable<GenomicBin> referenceCounts = CanvasIO.IterateThroughTextFile(referenceCountFile.FullName);

            using (var eFragment = fragmentCounts.GetEnumerator())
            using (var eReference = referenceCounts.GetEnumerator())
            using (var eRatio = ratios.GetEnumerator())
            using (StreamWriter writer = new StreamWriter(outputFile.FullName))
            {
                writer.WriteLine(CSVWriter.GetLine("Fragment Count", "Reference Count", "Chromosome",
                    "Start", "End", "Unsmoothed Log Ratio"));
                while (eFragment.MoveNext() && eReference.MoveNext() && eRatio.MoveNext())
                {
                    // Some bins could have been skipped when calculating the ratios
                    while (!eFragment.Current.IsSameBin(eRatio.Current))
                    {
                        if (!eFragment.MoveNext()) // Ran out of fragment bins
                            throw new ApplicationException("Fragment bins and ratio bins are not in the same order.");
                    }
                    while (!eReference.Current.IsSameBin(eRatio.Current))
                    {
                        if (!eReference.MoveNext()) // Ran out of reference bins
                            throw new ApplicationException("Reference bins and ratio bins are not in the same order.");
                    }
                    if (!eFragment.Current.IsSameBin(eReference.Current)
                        || !eFragment.Current.IsSameBin(eRatio.Current))
                    {
                        throw new ApplicationException("Bins are not in the same order.");
                    }
                    writer.WriteLine(CSVWriter.GetLine(eFragment.Current.Count.ToString(),
                        eReference.Current.Count.ToString(), eFragment.Current.Chromosome,
                        eFragment.Current.Start.ToString(), eFragment.Current.Stop.ToString(),
                        eRatio.Current.Count.ToString()));
                }
            }
        }
    }
}
