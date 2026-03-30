using System;

namespace ConsoleInk
{
    /// <summary>
    /// Defines the color and styling theme for Markdown rendering.
    /// </summary>
    public class ConsoleTheme
    {
        // --- Static Theme Instances ---

        private static readonly Lazy<ConsoleTheme> _defaultTheme = new Lazy<ConsoleTheme>(() => new ConsoleTheme());
        private static readonly Lazy<ConsoleTheme> _monochromeTheme = new Lazy<ConsoleTheme>(CreateMonochromeTheme);

        /// <summary>
        /// Gets the default theme with standard console colors.
        /// </summary>
        public static ConsoleTheme Default => _defaultTheme.Value;

        /// <summary>
        /// Gets a theme that uses no color, relying only on default foreground/background.
        /// </summary>
        public static ConsoleTheme Monochrome => _monochromeTheme.Value;

        // --- Theme Properties ---

        /// <summary>Gets or sets the color for Level 1 Headings.</summary>
        public ConsoleColor? Heading1Color { get; set; } = ConsoleColor.Yellow;
        /// <summary>Gets or sets the ANSI style code(s) for Level 1 Headings.</summary>
        public string Heading1Style { get; set; } = Ansi.Bold;
        /// <summary>Gets or sets the color for Level 2 Headings.</summary>
        public ConsoleColor? Heading2Color { get; set; } = ConsoleColor.Cyan;
        /// <summary>Gets or sets the ANSI style code(s) for Level 2 Headings.</summary>
        public string Heading2Style { get; set; } = Ansi.Underline;
        /// <summary>Gets or sets the color for Level 3 Headings.</summary>
        public ConsoleColor? Heading3Color { get; set; } = ConsoleColor.Green;
        /// <summary>Gets or sets the ANSI style code(s) for Level 3 Headings.</summary>
        public string Heading3Style { get; set; } = ""; // No default style for H3
        /// <summary>Gets or sets the color for Level 4 Headings.</summary>
        public ConsoleColor? Heading4Color { get; set; } = ConsoleColor.Magenta;
        /// <summary>Gets or sets the color for Level 5 Headings.</summary>
        public ConsoleColor? Heading5Color { get; set; } = ConsoleColor.Blue;
        /// <summary>Gets or sets the color for Level 6 Headings.</summary>
        public ConsoleColor? Heading6Color { get; set; } = ConsoleColor.DarkYellow;

        /// <summary>Gets or sets the foreground color for link text.</summary>
        public ConsoleColor? LinkForegroundColor { get; set; } = ConsoleColor.Blue;
        /// <summary>Gets or sets the complete ANSI style code(s) applied to link text (e.g., includes color and underline).</summary>
        public string LinkTextStyle { get; set; } = Ansi.Underline + Ansi.FgBlue;
        /// <summary>Gets or sets the complete ANSI style code(s) applied to link URLs (often dimmed).</summary>
        public string LinkUrlStyle { get; set; } = Ansi.Faint;
        /// <summary>Gets or sets the background color for link text (null for default).</summary>
        public ConsoleColor? LinkBackgroundColor { get; set; } = null;
        /// <summary>Gets or sets a value indicating whether link text should be underlined (redundant if LinkTextStyle includes underline).</summary>
        public bool LinkUnderline { get; set; } = true;

        /// <summary>Gets or sets the foreground color for code blocks.</summary>
        public ConsoleColor? CodeForegroundColor { get; set; } = ConsoleColor.Gray;
        /// <summary>Gets or sets the complete ANSI style code(s) applied to fenced code blocks.</summary>
        public string CodeBlockStyle { get; set; } = Ansi.FgBrightBlack;
        /// <summary>Gets or sets the background color for code blocks (null for default).</summary>
        public ConsoleColor? CodeBackgroundColor { get; set; } = null; // Default background often looks best

        /// <summary>Gets or sets the color for the blockquote marker and/or text.</summary>
        public ConsoleColor? BlockquoteColor { get; set; } = ConsoleColor.DarkGray;
        /// <summary>Gets or sets the color for horizontal rules.</summary>
        public ConsoleColor? HorizontalRuleColor { get; set; } = ConsoleColor.DarkGray;
        /// <summary>Gets or sets the color for list bullets/numbers.</summary>
        public ConsoleColor? ListBulletColor { get; set; } = ConsoleColor.Yellow; // Color for *, -, 1. etc.
        /// <summary>Gets or sets the prefix string used for blockquotes (before styling).</summary>
        public string BlockquotePrefix { get; set; } = "| ";
        /// <summary>Gets or sets the character used to draw horizontal rules.</summary>
        public char HorizontalRuleChar { get; set; } = '-';
        /// <summary>Gets or sets the prefix used for unordered list items.</summary>
        public string UnorderedListPrefix { get; set; } = "* ";
        /// <summary>Gets or sets the format string for ordered list item prefixes ({0} is replaced by the number).</summary>
        public string OrderedListPrefixFormat { get; set; } = "{0}. "; // {0} is the number

        /// <summary>Gets or sets the complete ANSI style code(s) for inline code spans.</summary>
        public string InlineCodeStyle { get; set; } = Ansi.Faint; // Dimmed text for inline code

        /// <summary>
        /// Gets or sets the style for the blockquote marker (e.g., '>').
        /// Defaults to Dim styling.
        /// </summary>
        public string BlockquoteMarkerStyle { get; set; } = Ansi.Faint;

        /// <summary>
        /// Gets or sets the marker character(s) used for blockquotes.
        /// Defaults to "> ".
        /// </summary>
        public string BlockquoteMarker { get; set; } = "> ";

        /// <summary>
        /// Gets or sets the style applied to the alt text of an image.
        /// Defaults to Dim styling.
        /// </summary>
        public string ImageAltTextStyle { get; set; } = Ansi.Faint;

        /// <summary>
        /// Gets or sets the prefix added before the alt text of an image.
        /// Defaults to "[Image: ".
        /// </summary>
        public string ImagePrefix { get; set; } = "[Image: ";

        /// <summary>
        /// Gets or sets the suffix added after the alt text of an image.
        /// Defaults to "]".
        /// </summary>
        public string ImageSuffix { get; set; } = "]";

        /// <summary>
        /// Gets or sets the style for footnote reference markers in text (e.g., [1]).
        /// Defaults to Cyan + Bold.
        /// </summary>
        public string FootnoteReferenceStyle { get; set; } = Ansi.FgCyan + Ansi.Bold;

        /// <summary>
        /// Gets or sets the style for footnote definition labels.
        /// Defaults to Cyan.
        /// </summary>
        public string FootnoteDefinitionLabelStyle { get; set; } = Ansi.FgCyan;

        /// <summary>
        /// Gets or sets the style for definition list terms.
        /// Defaults to Bold + Yellow.
        /// </summary>
        public string DefinitionTermStyle { get; set; } = Ansi.Bold + Ansi.FgYellow;

        /// <summary>
        /// Gets or sets the style for subscript text (~text~).
        /// Defaults to Cyan foreground.
        /// </summary>
        public string SubscriptStyle { get; set; } = Ansi.FgCyan;

        /// <summary>
        /// Gets or sets the style for superscript text (^text^).
        /// Defaults to Cyan foreground.
        /// </summary>
        public string SuperscriptStyle { get; set; } = Ansi.FgCyan;

        /// <summary>
        /// Gets or sets the style for inserted text (++text++).
        /// Defaults to Underline.
        /// </summary>
        public string InsertedStyle { get; set; } = Ansi.Underline;

        /// <summary>
        /// Gets or sets the style for marked/highlighted text (==text==).
        /// Defaults to Reverse Video.
        /// </summary>
        public string MarkedStyle { get; set; } = Ansi.ReverseVideo;

        /// <summary>
        /// Gets or sets the style for citation text ("text").
        /// Defaults to Italic.
        /// </summary>
        public string CitationStyle { get; set; } = Ansi.Italic;

        /// <summary>
        /// Gets or sets the style for math expressions ($..$ and $$..$$).
        /// Defaults to Green + Italic.
        /// </summary>
        public string MathStyle { get; set; } = Ansi.FgGreen + Ansi.Italic;

        /// <summary>
        /// Gets or sets the style for abbreviation text.
        /// Defaults to Underline.
        /// </summary>
        public string AbbreviationStyle { get; set; } = Ansi.Underline;

        /// <summary>
        /// Gets or sets the style for custom container borders.
        /// Defaults to Bright Black (gray).
        /// </summary>
        public string CustomContainerBorderStyle { get; set; } = Ansi.FgBrightBlack;

        /// <summary>
        /// Gets or sets the marker string used for an unchecked task list item.
        /// Defaults to "[ ] ".
        /// </summary>
        public string TaskListUncheckedMarker { get; set; } = "[ ] ";

        /// <summary>
        /// Gets or sets the marker string used for a checked task list item.
        /// Defaults to "[x] ".
        /// </summary>
        public string TaskListCheckedMarker { get; set; } = "[x] ";

        // --- Emphasis Styles ---
        /// <summary>Gets or sets the ANSI style code(s) for bold emphasis.</summary>
        public string BoldStyle { get; set; } = Ansi.Bold;
        /// <summary>Gets or sets the ANSI style code(s) for italic emphasis.</summary>
        public string ItalicStyle { get; set; } = Ansi.Italic;
        /// <summary>Gets or sets the ANSI style code(s) for strikethrough emphasis.</summary>
        public string StrikethroughStyle { get; set; } = Ansi.Strikethrough;

        // TODO: Add more theme properties

        /// <summary>
        /// Creates a monochrome theme instance.
        /// </summary>
        private static ConsoleTheme CreateMonochromeTheme()
        {
            // Use null for colors to indicate default console foreground/background
            return new ConsoleTheme
            {
                Heading1Color = null,
                Heading2Color = null,
                Heading3Color = null,
                Heading4Color = null,
                Heading5Color = null,
                Heading6Color = null,
                LinkForegroundColor = null,
                LinkBackgroundColor = null,
                LinkUnderline = false, // Underlining might not render well without color
                LinkTextStyle = "",
                LinkUrlStyle = "",
                CodeForegroundColor = null,
                CodeBackgroundColor = null,
                CodeBlockStyle = "",
                BlockquoteColor = null,
                HorizontalRuleColor = null,
                ListBulletColor = null,
                BlockquotePrefix = "| ",
                HorizontalRuleChar = '-',
                UnorderedListPrefix = "* ",
                OrderedListPrefixFormat = "{0}. ",
                BoldStyle = "",
                ItalicStyle = "",
                StrikethroughStyle = "",
                FootnoteReferenceStyle = "",
                FootnoteDefinitionLabelStyle = "",
                DefinitionTermStyle = "",
                SubscriptStyle = "",
                SuperscriptStyle = "",
                InsertedStyle = "",
                MarkedStyle = "",
                CitationStyle = "",
                MathStyle = "",
                AbbreviationStyle = "",
                CustomContainerBorderStyle = "",
            };
        }
    }
}
