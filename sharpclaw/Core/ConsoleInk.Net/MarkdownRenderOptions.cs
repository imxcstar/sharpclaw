using System;

namespace ConsoleInk
{
    /// <summary>
    /// Provides options for configuring Markdown rendering behavior.
    /// </summary>
    public class MarkdownRenderOptions
    {
        /// <summary>
        /// Gets or sets the width (in characters) to use for word wrapping.
        /// Defaults to Console.WindowWidth if available, otherwise 80.
        /// </summary>
        public int ConsoleWidth { get; set; } = GetDefaultConsoleWidth();

        /// <summary>
        /// Gets or sets a value indicating whether ANSI color codes should be included in the output.
        /// Defaults to true.
        /// </summary>
        public bool EnableColors { get; set; } = true;

        /// <summary>
        /// Gets or sets the theme to use for styling.
        /// Defaults to ConsoleTheme.Default.
        /// </summary>
        public ConsoleTheme Theme { get; set; } = ConsoleTheme.Default;

        /// <summary>
        /// Gets or sets a value indicating whether raw HTML tags should be stripped from the output.
        /// Defaults to true.
        /// </summary>
        public bool StripHtml { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether links should be rendered using OSC 8 hyperlink escape sequences.
        /// If false (default), links are rendered with standard ANSI styling (color/underline).
        /// If true, links are rendered as clickable hyperlinks in supported terminals.
        /// </summary>
        public bool UseHyperlinks { get; set; } = false;

        // TODO: Add more configuration options as needed.

        /// <summary>
        /// Initializes a new instance of the <see cref="MarkdownRenderOptions"/> class with default values.
        /// </summary>
        public MarkdownRenderOptions()
        {
            try
            {
                ConsoleWidth = Console.WindowWidth;
            }
            catch (System.IO.IOException)
            {
                // Handle cases where Console.WindowWidth is unavailable (e.g., redirected output)
                ConsoleWidth = 80;
            }
        }

        private static int GetDefaultConsoleWidth()
        {
            try
            {
                return Console.WindowWidth;
            }
            catch (System.IO.IOException)
            {
                // Handle cases where Console.WindowWidth is unavailable (e.g., redirected output)
                return 80;
            }
        }
    }
}
