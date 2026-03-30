using System.IO;
using System.Text;

namespace ConsoleInk
{
    /// <summary>
    /// Provides static helper methods for rendering Markdown directly to the console or a string.
    /// </summary>
    public static class MarkdownConsole
    {
        /// <summary>
        /// Renders the specified Markdown text to a string with ANSI escape codes.
        /// </summary>
        /// <param name="markdownText">The Markdown text to render.</param>
        /// <param name="options">Optional configuration for rendering.</param>
        /// <returns>A string containing the rendered output with ANSI codes.</returns>
        public static string Render(string markdownText, MarkdownRenderOptions? options = null)
        {
            if (markdownText == null) return string.Empty;

            var stringBuilder = new StringBuilder();
            using (var stringWriter = new StringWriter(stringBuilder))
            {
                Render(markdownText, stringWriter, options);
            }
            return stringBuilder.ToString();
        }

        /// <summary>
        /// Renders the specified Markdown text directly to the provided TextWriter.
        /// </summary>
        /// <param name="markdownText">The Markdown text to render.</param>
        /// <param name="outputWriter">The TextWriter to write the formatted output to.</param>
        /// <param name="options">Optional configuration for rendering.</param>
        public static void Render(string markdownText, TextWriter outputWriter, MarkdownRenderOptions? options = null)
        {
            if (markdownText == null || outputWriter == null) return;

            using (var markdownWriter = new MarkdownConsoleWriter(outputWriter, options))
            {
                markdownWriter.Write(markdownText);
                // Complete() is called implicitly by Dispose/using
            }
        }

        /// <summary>
        /// Renders Markdown content read from a TextReader to the specified TextWriter.
        /// </summary>
        /// <param name="markdownReader">The TextReader providing the Markdown content.</param>
        /// <param name="outputWriter">The TextWriter to write the formatted output to.</param>
        /// <param name="options">Optional configuration for rendering.</param>
        public static void Render(TextReader markdownReader, TextWriter outputWriter, MarkdownRenderOptions? options = null)
        {
            if (markdownReader == null || outputWriter == null) return;

            using (var markdownWriter = new MarkdownConsoleWriter(outputWriter, options))
            {
                // Stream content from reader to writer
                // Use CopyToAsync in a real async scenario, but for simplicity:
                char[] buffer = new char[4096];
                int charsRead;
                while ((charsRead = markdownReader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    markdownWriter.Write(buffer, 0, charsRead);
                }
                 // Complete() is called implicitly by Dispose/using
            }
        }
    }
}
