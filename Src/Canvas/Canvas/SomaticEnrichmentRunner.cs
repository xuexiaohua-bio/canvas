using Canvas.CommandLineParsing;
using CanvasCommon;
using Illumina.SecondaryAnalysis;
using Isas.SequencingFiles;
using Isas.Shared.Checkpointing;
using Isas.Shared.Utilities;
using Isas.Shared.Utilities.FileSystem;

namespace Canvas
{
    public class SomaticEnrichmentRunner : IModeRunner
    {
        private readonly SomaticEnrichmentOptions _somaticEnrichmentOptions;

        public SomaticEnrichmentRunner(CommonOptions commonOptions, SomaticEnrichmentOptions somaticEnrichmentOptions)
        {
            _somaticEnrichmentOptions = somaticEnrichmentOptions;
            CommonOptions = commonOptions;
        }

        public CommonOptions CommonOptions { get; }
        public void Run(ILogger logger, ICheckpointRunnerAsync checkpointRunner, IWorkManager workManager)
        {
            CanvasRunner runner = new CanvasRunner(logger, workManager, checkpointRunner, true, CanvasCoverageMode.TruncatedDynamicRange, 300, CommonOptions.CustomParams);
            var callset = GetCallset(logger);
            runner.CallSample(callset);
        }

        private CanvasCallset GetCallset(ILogger logger)
        {
            IFileLocation outputVcfPath = CommonOptions.OutputDirectory.GetFileLocation("CNV.vcf.gz");
            var manifest = new NexteraManifest(_somaticEnrichmentOptions.Manifest.FullName, null, logger.Error);
            // TODO: refactor and remove the following two lines
            manifest.CanvasControlBinnedPath = _somaticEnrichmentOptions.ControlBinned?.FullName;
            manifest.CanvasBinSize = _somaticEnrichmentOptions.ControlBinSize;
            CanvasCallset callSet = new CanvasCallset(
                    _somaticEnrichmentOptions.Bam,
                    CommonOptions.SampleName,
                    CommonOptions.WholeGenomeFasta,
                    CommonOptions.OutputDirectory,
                    CommonOptions.KmerFasta,
                    CommonOptions.FilterBed,
                    CommonOptions.PloidyBed,
                    CommonOptions.BAlleleSites,
                    CommonOptions.IsDbSnpVcf,
                    _somaticEnrichmentOptions.ControlBams,
                    manifest,
                    null,
                    outputVcfPath);
            return callSet;
        }
    }
}