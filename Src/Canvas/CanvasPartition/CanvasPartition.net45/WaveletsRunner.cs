using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CanvasCommon;

namespace CanvasPartition
{
    class WaveletsRunner
    {
        private WaveletsRunnerParams _parameters;
        public static readonly double DefaultMadFactor = 2.0; // default MAD factor for Wavelets

        public class WaveletsRunnerParams
        {
            public bool IsGermline { get; }
            public string CommonCNVs { get; }
            public double ThresholdLower { get; }
            public double ThresholdUpper { get; }
            public double MadFactor { get; }
            public int MinSize { get; }
            public int Verbose { get; }
            public bool IsSmallPedegree { get; }

            public WaveletsRunnerParams(bool isGermline, string commonCNVs, double thresholdLower = 5, 
                double thresholdUpper = 80, double madFactor = 2, int minSize = 10, 
                int verbose = 1, bool isSmallPedegree = false)
            {
                IsGermline = isGermline;
                CommonCNVs = commonCNVs;
                ThresholdLower = thresholdLower;
                ThresholdUpper = thresholdUpper;
                MadFactor = madFactor;
                MinSize = minSize;
                Verbose = verbose;
                IsSmallPedegree = isSmallPedegree;
            }
        }

        public WaveletsRunner(WaveletsRunnerParams waveletsRunnerParams)
        {
            _parameters = waveletsRunnerParams;
        }

        /// <summary>
        /// Wavelets: unbalanced HAAR wavelets segmentation 
        /// </summary>
        public Dictionary<string, Segmentation.Segment[]> Run(Segmentation segmentationEngine)
        {
            bool useVaf = true;
            if (useVaf)
            {
                var tmpVafByChr = new Dictionary<string, double[]>();
                var tmpVaftoCoverageIndexByChr = new Dictionary<string, int[]>();

                foreach (string chr in segmentationEngine.VafByChr.Keys) { 
                    tmpVafByChr[chr] = segmentationEngine.VafByChr[chr].Select(coverageToVafMapper => coverageToVafMapper.Vaf).ToArray();
                    tmpVaftoCoverageIndexByChr[chr] = segmentationEngine.VafByChr[chr].Select(coverageToVafMapper => coverageToVafMapper.Index).ToArray();
                }
                return LaunchWavelets(tmpVafByChr, segmentationEngine.StartByChr,
                    segmentationEngine.EndByChr, tmpVaftoCoverageIndexByChr);
            }

            return LaunchWavelets(segmentationEngine.CoverageByChr, segmentationEngine.StartByChr,
                segmentationEngine.EndByChr);
        }

        public Dictionary<string, Segmentation.Segment[]> LaunchWavelets(Dictionary<string, double[]> coverageByChr, Dictionary<string, uint[]> startByChr, 
            Dictionary<string, uint[]> endByChr, Dictionary<string, int[]> vafToCoverageIndex = null)
        {
            var inaByChr = new Dictionary<string, int[]>();
            var finiteScoresByChr = new Dictionary<string, double[]>();

            var tasks = coverageByChr.Select(scoreByChrKVP => new ThreadStart(() =>
            {
                string chr = scoreByChrKVP.Key;
                Helper.GetFiniteIndices(scoreByChrKVP.Value, out int[] ina); // not NaN, -Inf, Inf

                double[] scores;
                if (ina.Length == scoreByChrKVP.Value.Length)
                    scores = scoreByChrKVP.Value;
                else
                    Helper.ExtractValues<double>(scoreByChrKVP.Value, ina, out scores);

                lock (finiteScoresByChr)
                {
                    finiteScoresByChr[chr] = scores;
                    inaByChr[chr] = ina;
                }
            })).ToList();


            Parallel.ForEach(tasks, task => task.Invoke());
            // Quick sanity-check: If we don't have any segments, then return a dummy result.
            int n = finiteScoresByChr.Values.Sum(list => list.Length);
            if (n == 0)
                return new Dictionary<string, Segmentation.Segment[]>();           

            var segmentByChr = new Dictionary<string, Segmentation.Segment[]>();
            // load common CNV segments
            Dictionary<string, List<SampleGenomicBin>> commonCNVintervals = null;
            if (_parameters.CommonCNVs != null)
            {
                commonCNVintervals = CanvasCommon.Utilities.LoadBedFile(_parameters.CommonCNVs);
                CanvasCommon.Utilities.SortAndOverlapCheck(commonCNVintervals, _parameters.CommonCNVs);
            }

            tasks = coverageByChr.Keys.Select(chr => new ThreadStart(() =>
            {
                var breakpoints = new List<int>();
                int segmentLengthByChr = coverageByChr[chr].Length;
                if (segmentLengthByChr > _parameters.MinSize)
                {
                    WaveletSegmentation.HaarWavelets(coverageByChr[chr], _parameters.ThresholdLower, _parameters.ThresholdUpper, 
                        breakpoints, _parameters.IsGermline, madFactor: _parameters.MadFactor);
                }


                if (vafToCoverageIndex[chr] != null) {
                    if (breakpoints.Max() > vafToCoverageIndex.Count -1)
                        throw new Exception($"breakpoint {breakpoints.Max()} is larger then the zero-based size of the " +
                                            $"vafToCoverageIndex '{vafToCoverageIndex.Count - 1}'");
                    breakpoints = breakpoints.Select(breakpoint => vafToCoverageIndex[chr][breakpoint]).ToList();
                }


                if (_parameters.CommonCNVs != null && commonCNVintervals.ContainsKey(chr))
                {
                    var remappedCommonCNVintervals = Segmentation.RemapCommonRegions(commonCNVintervals[chr],
                        startByChr[chr], endByChr[chr]);
                    var oldbreakpoints = breakpoints;
                    breakpoints = Segmentation.OverlapCommonRegions(oldbreakpoints, remappedCommonCNVintervals);
                }

                var segments = Segmentation.DeriveSegments(breakpoints, segmentLengthByChr, startByChr[chr], endByChr[chr]);
                lock (segmentByChr)
                {
                    segmentByChr[chr] = segments;
                }
            })).ToList();

            Console.WriteLine("{0} Launching wavelet tasks", DateTime.Now);
            Parallel.ForEach(tasks, task => task.Invoke());
            Console.WriteLine("{0} Completed wavelet tasks", DateTime.Now);
            Console.WriteLine("{0} Segmentation results complete", DateTime.Now);
            return segmentByChr;
        }
    }
}