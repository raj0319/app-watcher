using Xunit;

// the learner keeps two process-wide settings (Titles.MaskQuoted, Titles.MaskPatterns); tests that set them must not overlap
[assembly: CollectionBehavior(DisableTestParallelization = true)]
