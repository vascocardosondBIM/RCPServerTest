using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace RevitSketchPoC.Phase1_VectorExtraction.Utils
{
    public static class PythonProcessRunner
    {
        public static void Run(string arguments, int timeoutMs, string failureMessage) =>
            _ = RunReturningStdout(arguments, timeoutMs, failureMessage);

        /// <summary>Executa Python e devolve stdout completo (útil para diagnosticar ou ler caminho de índice).</summary>
        public static string RunReturningStdout(string arguments, int timeoutMs, string failureMessage)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var stdOut = new StringBuilder();
            var stdErr = new StringBuilder();

            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        stdOut.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                    {
                        stdErr.AppendLine(e.Data);
                    }
                };

                if (!process.Start())
                {
                    throw new InvalidOperationException("Falha ao iniciar o processo Python.");
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { /* ignored */ }
                    throw new InvalidOperationException("Timeout ao executar Python (" + timeoutMs + "ms).");
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        failureMessage + Environment.NewLine +
                        "Confirma Python + PyMuPDF (`python -m pip install pymupdf`)." + Environment.NewLine +
                        "stderr: " + (stdErr.Length == 0 ? "(vazio)" : stdErr.ToString().Trim()) + Environment.NewLine +
                        "stdout: " + (stdOut.Length == 0 ? "(vazio)" : stdOut.ToString().Trim()));
                }
            }

            return stdOut.ToString();
        }

        public static string ReadTextPreview(string path, int maxChars)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
            var buffer = new char[maxChars];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            var content = new string(buffer, 0, read);
            if (stream.Length > maxChars)
            {
                content += Environment.NewLine + Environment.NewLine +
                           "... (preview truncado; abre a pasta de output para o ficheiro completo)";
            }

            return content;
        }
    }
}
