using Newtonsoft.Json;
using RevitSketchPoC.Sketch.Contracts;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RevitSketchPoC.Sketch.Services
{
    /// <summary>
    /// Safe persistence for spike pipeline jobs in disk.
    /// Convention: %TEMP%/RevitSketchPoC/jobs/{job_id}/job.json
    /// </summary>
    public static class JobStateStore
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore
        };

        public static SpikePipelineJobState CreateNew(
            SpikePipelineJobInput input,
            string? fixedJobId = null)
        {
            var jobId = string.IsNullOrWhiteSpace(fixedJobId)
                ? BuildJobId()
                : NormalizeJobId(fixedJobId);

            var paths = BuildPaths(jobId);
            EnsureDirectories(paths);

            var state = new SpikePipelineJobState
            {
                JobId = jobId,
                Status = JobRunStatus.Pending,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Input = input ?? new SpikePipelineJobInput(),
                Paths = paths
            };

            state.Steps = JobStepNames.OrderedDefaults
                .Select((name, idx) => new SpikePipelineStepState
                {
                    Name = name,
                    Order = idx + 1,
                    Status = JobRunStatus.Pending
                })
                .ToList();

            Save(state);
            return state;
        }

        public static SpikePipelineJobState Load(string jobId)
        {
            var normalized = NormalizeJobId(jobId);
            var paths = BuildPaths(normalized);
            if (!File.Exists(paths.JobFilePath))
            {
                throw new FileNotFoundException("job.json não encontrado para job_id.", paths.JobFilePath);
            }

            var json = ReadAllTextSafe(paths.JobFilePath);
            SpikePipelineJobState? state;
            try
            {
                state = JsonConvert.DeserializeObject<SpikePipelineJobState>(json, JsonSettings);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("job.json inválido (JSON malformado).", ex);
            }

            if (state == null)
            {
                throw new InvalidOperationException("job.json inválido: objeto vazio.");
            }

            NormalizeLoadedState(state, paths);
            return state;
        }

        public static bool TryLoad(string jobId, out SpikePipelineJobState? state)
        {
            state = null;
            try
            {
                state = Load(jobId);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Save(SpikePipelineJobState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            state.JobId = NormalizeJobId(state.JobId);
            state.UpdatedAtUtc = DateTime.UtcNow;
            state.Paths = BuildPaths(state.JobId);
            EnsureDirectories(state.Paths);

            if (state.Steps == null || state.Steps.Count == 0)
            {
                state.Steps = JobStepNames.OrderedDefaults
                    .Select((name, idx) => new SpikePipelineStepState
                    {
                        Name = name,
                        Order = idx + 1,
                        Status = JobRunStatus.Pending
                    })
                    .ToList();
            }

            var json = JsonConvert.SerializeObject(state, JsonSettings);
            WriteAllTextAtomic(state.Paths.JobFilePath, json);
        }

        public static string GetJobsRootPath()
        {
            return Path.Combine(Path.GetTempPath(), "RevitSketchPoC", "jobs");
        }

        public static string GetJobDirectoryPath(string jobId)
        {
            var normalized = NormalizeJobId(jobId);
            return Path.Combine(GetJobsRootPath(), normalized);
        }

        public static string GetJobFilePath(string jobId)
        {
            return Path.Combine(GetJobDirectoryPath(jobId), "job.json");
        }

        public static string GetArtifactsDirectoryPath(string jobId)
        {
            return Path.Combine(GetJobDirectoryPath(jobId), "artifacts");
        }

        public static string GetLogsDirectoryPath(string jobId)
        {
            return Path.Combine(GetJobDirectoryPath(jobId), "logs");
        }

        private static SpikePipelineJobPaths BuildPaths(string normalizedJobId)
        {
            var root = GetJobsRootPath();
            var dir = Path.Combine(root, normalizedJobId);
            return new SpikePipelineJobPaths
            {
                JobsRootPath = root,
                JobDirectoryPath = dir,
                JobFilePath = Path.Combine(dir, "job.json"),
                ArtifactsDirectoryPath = Path.Combine(dir, "artifacts"),
                LogsDirectoryPath = Path.Combine(dir, "logs")
            };
        }

        private static void EnsureDirectories(SpikePipelineJobPaths paths)
        {
            Directory.CreateDirectory(paths.JobsRootPath);
            Directory.CreateDirectory(paths.JobDirectoryPath);
            Directory.CreateDirectory(paths.ArtifactsDirectoryPath);
            Directory.CreateDirectory(paths.LogsDirectoryPath);
        }

        private static void NormalizeLoadedState(SpikePipelineJobState state, SpikePipelineJobPaths paths)
        {
            state.JobId = NormalizeJobId(state.JobId);
            state.Paths = paths;
            state.Input ??= new SpikePipelineJobInput();
            state.Spike1Artifacts ??= new SpikePipelineSpike1Artifacts();
            state.FinalOutputs ??= new SpikePipelineFinalOutputs();
            state.Steps ??= new System.Collections.Generic.List<SpikePipelineStepState>();
            foreach (var step in state.Steps)
            {
                step.Artifacts ??= new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                step.Metrics ??= new SpikePipelineStepMetrics();
            }
        }

        private static string BuildJobId()
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var shortGuid = Guid.NewGuid().ToString("N").Substring(0, 8);
            return "job_" + stamp + "_" + shortGuid;
        }

        private static string NormalizeJobId(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new InvalidOperationException("job_id inválido.");
            }

            var normalized = Regex.Replace(jobId.Trim(), @"[^A-Za-z0-9_\-]", "_");
            if (normalized.Length > 80)
            {
                normalized = normalized.Substring(0, 80);
            }

            if (string.IsNullOrWhiteSpace(normalized))
            {
                throw new InvalidOperationException("job_id inválido após normalização.");
            }

            return normalized;
        }

        private static string ReadAllTextSafe(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                return reader.ReadToEnd();
            }
        }

        private static void WriteAllTextAtomic(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(dir))
            {
                throw new InvalidOperationException("Path inválido para escrita de job.json.");
            }

            Directory.CreateDirectory(dir);

            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(path))
            {
                // Use Replace when available to reduce chance of partial writes.
                var backup = path + ".bak";
                try
                {
                    File.Replace(tmp, path, backup, ignoreMetadataErrors: true);
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }
                }
                catch
                {
                    File.Copy(tmp, path, overwrite: true);
                    File.Delete(tmp);
                }
            }
            else
            {
                File.Move(tmp, path);
            }
        }
    }
}
