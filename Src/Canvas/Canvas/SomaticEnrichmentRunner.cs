using System;
using Canvas.CommandLineParsing;
using Canvas.SmallPedigree;
using CanvasCommon;
using Illumina.Common.FileSystem;
using Isas.Framework.Checkpointing;
using Isas.Framework.Logging;
using Isas.Framework.WorkManagement;
using Isas.Framework.WorkManagement.CommandBuilding;
using Isas.Manifests.NexteraManifest;

namespace Canvas
{
    public class SomaticEnrichmentRunner : IModeRunner
    {
        private readonly SomaticEnrichmentOptions _somaticEnrichmentOptions;

        public SomaticEnrichmentRunner(SomaticEnrichmentInput input)
        {
            _somaticEnrichmentOptions = input.SomaticEnrichmentOptions;
            CommonOptions = input.CommonOptions;
            SingleSampleCommonOptions = input.SingleSampleCommonOptions;
        }

        public CommonOptions CommonOptions { get; }
        public SingleSampleCommonOptions SingleSampleCommonOptions { get; }

        public void Run(CanvasRunnerFactory runnerFactory)
        {
            var runner = runnerFactory.Create(true, CanvasCoverageMode.TruncatedDynamicRange, 300, CommonOptions.CustomParams);
            var callset = GetCallset(runner.Logger);
            runner.CallSample(callset);
        }

        private CanvasCallset GetCallset(ILogger logger)
        {
            AnalysisDetails analysisDetails = new AnalysisDetails(CommonOptions.OutputDirectory,CommonOptions.WholeGenomeFasta, 
                CommonOptions.KmerFasta,CommonOptions.FilterBed, SingleSampleCommonOptions.PloidyVcf, null);
            IFileLocation outputVcfPath = CommonOptions.OutputDirectory.GetFileLocation("CNV.vcf.gz");
            var manifest = new NexteraManifest(_somaticEnrichmentOptions.Manifest.FullName, null, logger.Error);
            // TODO: refactor and remove the following two lines
            manifest.CanvasControlBinnedPath = _somaticEnrichmentOptions.ControlBinned?.FullName;
            manifest.CanvasBinSize = _somaticEnrichmentOptions.ControlBinSize;
            CanvasCallset callSet = new CanvasCallset(
                    _somaticEnrichmentOptions.Bam,
                    SingleSampleCommonOptions.SampleName,
                    SingleSampleCommonOptions.BAlleleSites,
                    SingleSampleCommonOptions.IsDbSnpVcf,
                    _somaticEnrichmentOptions.ControlBams,
                    manifest,
                    null,
                    outputVcfPath,
                    analysisDetails);
            return callSet;
        }
    }
}