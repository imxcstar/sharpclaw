using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleInk
{
    /// <summary>
    /// Provides common ANSI escape code constants for terminal formatting.
    /// Reference: https://en.wikipedia.org/wiki/ANSI_escape_code#SGR_(Select_Graphic_Rendition)_parameters
    /// </summary>
    public static class Ansi
    {
        private const string Esc = "\x1B"; // Or "\u001B"
        private const string Csi = Esc + "[";

        // --- General ---
        /// <summary>
        /// Resets all attributes to their default state.
        /// </summary>
        public const string Reset = Csi + "0m";

        // --- Styles ---
        /// <summary>
        /// Applies bold (increased intensity) style.
        /// </summary>
        public const string Bold = Csi + "1m";
        /// <summary>
        /// Applies faint (decreased intensity) style. (Not widely supported)
        /// </summary>
        public const string Faint = Csi + "2m"; // Not widely supported
        /// <summary>
        /// Applies italic style. (Not widely supported)
        /// </summary>
        public const string Italic = Csi + "3m"; // Not widely supported
        /// <summary>
        /// Applies underline style.
        /// </summary>
        public const string Underline = Csi + "4m";
        /// <summary>
        /// Applies slow blink style. (Not widely supported)
        /// </summary>
        public const string SlowBlink = Csi + "5m"; // Not widely supported
        /// <summary>
        /// Applies rapid blink style. (Not widely supported)
        /// </summary>
        public const string RapidBlink = Csi + "6m"; // Not widely supported
        /// <summary>
        /// Swaps foreground and background colors.
        /// </summary>
        public const string ReverseVideo = Csi + "7m";
        /// <summary>
        /// Hides text. (Not widely supported)
        /// </summary>
        public const string Conceal = Csi + "8m"; // Not widely supported
        /// <summary>
        /// Applies strikethrough style. (Not widely supported)
        /// </summary>
        public const string Strikethrough = Csi + "9m"; // Not widely supported

        /// <summary>
        /// Turns off bold and faint styles.
        /// </summary>
        public const string BoldOff = Csi + "22m"; // Neither bold nor faint
        /// <summary>
        /// Turns off italic style.
        /// </summary>
        public const string ItalicOff = Csi + "23m";
        /// <summary>
        /// Turns off underline style.
        /// </summary>
        public const string UnderlineOff = Csi + "24m";
        /// <summary>
        /// Turns off blink style.
        /// </summary>
        public const string BlinkOff = Csi + "25m";
        /// <summary>
        /// Turns off reverse video style.
        /// </summary>
        public const string ReverseVideoOff = Csi + "27m";
        /// <summary>
        /// Turns off conceal style.
        /// </summary>
        public const string ConcealOff = Csi + "28m";
        /// <summary>
        /// Turns off strikethrough style.
        /// </summary>
        public const string StrikethroughOff = Csi + "29m";


        // --- Foreground Colors (3-bit / 4-bit) ---
        /// <summary>Sets foreground color to Black.</summary>
        public const string FgBlack = Csi + "30m";
        /// <summary>Sets foreground color to Red.</summary>
        public const string FgRed = Csi + "31m";
        /// <summary>Sets foreground color to Green.</summary>
        public const string FgGreen = Csi + "32m";
        /// <summary>Sets foreground color to Yellow.</summary>
        public const string FgYellow = Csi + "33m";
        /// <summary>Sets foreground color to Blue.</summary>
        public const string FgBlue = Csi + "34m";
        /// <summary>Sets foreground color to Magenta.</summary>
        public const string FgMagenta = Csi + "35m";
        /// <summary>Sets foreground color to Cyan.</summary>
        public const string FgCyan = Csi + "36m";
        /// <summary>Sets foreground color to White.</summary>
        public const string FgWhite = Csi + "37m";
        /// <summary>Resets foreground color to the default.</summary>
        public const string FgDefault = Csi + "39m";

        // --- Bright Foreground Colors (3-bit / 4-bit) ---
        /// <summary>Sets foreground color to Bright Black (Gray).</summary>
        public const string FgBrightBlack = Csi + "90m"; // Gray
        /// <summary>Sets foreground color to Bright Red.</summary>
        public const string FgBrightRed = Csi + "91m";
        /// <summary>Sets foreground color to Bright Green.</summary>
        public const string FgBrightGreen = Csi + "92m";
        /// <summary>Sets foreground color to Bright Yellow.</summary>
        public const string FgBrightYellow = Csi + "93m";
        /// <summary>Sets foreground color to Bright Blue.</summary>
        public const string FgBrightBlue = Csi + "94m";
        /// <summary>Sets foreground color to Bright Magenta.</summary>
        public const string FgBrightMagenta = Csi + "95m";
        /// <summary>Sets foreground color to Bright Cyan.</summary>
        public const string FgBrightCyan = Csi + "96m";
        /// <summary>Sets foreground color to Bright White.</summary>
        public const string FgBrightWhite = Csi + "97m";


        // --- Background Colors (3-bit / 4-bit) ---
        /// <summary>Sets background color to Black.</summary>
        public const string BgBlack = Csi + "40m";
        /// <summary>Sets background color to Red.</summary>
        public const string BgRed = Csi + "41m";
        /// <summary>Sets background color to Green.</summary>
        public const string BgGreen = Csi + "42m";
        /// <summary>Sets background color to Yellow.</summary>
        public const string BgYellow = Csi + "43m";
        /// <summary>Sets background color to Blue.</summary>
        public const string BgBlue = Csi + "44m";
        /// <summary>Sets background color to Magenta.</summary>
        public const string BgMagenta = Csi + "45m";
        /// <summary>Sets background color to Cyan.</summary>
        public const string BgCyan = Csi + "46m";
        /// <summary>Sets background color to White.</summary>
        public const string BgWhite = Csi + "47m";
        /// <summary>Resets background color to the default.</summary>
        public const string BgDefault = Csi + "49m";

        // --- Bright Background Colors (3-bit / 4-bit) ---
        /// <summary>Sets background color to Bright Black (Gray).</summary>
        public const string BgBrightBlack = Csi + "100m"; // Gray
        /// <summary>Sets background color to Bright Red.</summary>
        public const string BgBrightRed = Csi + "101m";
        /// <summary>Sets background color to Bright Green.</summary>
        public const string BgBrightGreen = Csi + "102m";
        /// <summary>Sets background color to Bright Yellow.</summary>
        public const string BgBrightYellow = Csi + "103m";
        /// <summary>Sets background color to Bright Blue.</summary>
        public const string BgBrightBlue = Csi + "104m";
        /// <summary>Sets background color to Bright Magenta.</summary>
        public const string BgBrightMagenta = Csi + "105m";
        /// <summary>Sets background color to Bright Cyan.</summary>
        public const string BgBrightCyan = Csi + "106m";
        /// <summary>Sets background color to Bright White.</summary>
        public const string BgBrightWhite = Csi + "107m";


        // --- 256 Colors (8-bit) ---
        // Format: CSI 38;5;{code}m (Foreground)
        // Format: CSI 48;5;{code}m (Background)
        /// <summary>Generates the ANSI escape code for a 256-color palette foreground color.</summary>
        /// <param name="code">The 8-bit color code (0-255).</param>
        /// <returns>The ANSI escape sequence string.</returns>
        public static string FgColor256(byte code) => $"{Csi}38;5;{code}m";
        /// <summary>Generates the ANSI escape code for a 256-color palette background color.</summary>
        /// <param name="code">The 8-bit color code (0-255).</param>
        /// <returns>The ANSI escape sequence string.</returns>
        public static string BgColor256(byte code) => $"{Csi}48;5;{code}m";

        // --- True Color (24-bit) ---
        // Format: CSI 38;2;{r};{g};{b}m (Foreground)
        // Format: CSI 48;2;{r};{g};{b}m (Background)
        /// <summary>Generates the ANSI escape code for a 24-bit ("true color") foreground color.</summary>
        /// <param name="r">The red component (0-255).</param>
        /// <param name="g">The green component (0-255).</param>
        /// <param name="b">The blue component (0-255).</param>
        /// <returns>The ANSI escape sequence string.</returns>
        public static string FgColorTrue(byte r, byte g, byte b) => $"{Csi}38;2;{r};{g};{b}m";
        /// <summary>Generates the ANSI escape code for a 24-bit ("true color") background color.</summary>
        /// <param name="r">The red component (0-255).</param>
        /// <param name="g">The green component (0-255).</param>
        /// <param name="b">The blue component (0-255).</param>
        /// <returns>The ANSI escape sequence string.</returns>
        public static string BgColorTrue(byte r, byte g, byte b) => $"{Csi}48;2;{r};{g};{b}m";

        // --- OSC 8 Hyperlinks ---
        // Format: OSC 8 ; params ; URI ST
        // OSC: ESC ]
        // ST: ESC \ (represented as Esc + @"\")
        private const string Osc = Esc + "]";
        private const string St = Esc + @"\"; // String Terminator
        private const string Bel = "\a"; // Bell character as alternative terminator

        /// <summary>
        /// Generates the OSC 8 sequence to start a hyperlink.
        /// </summary>
        /// <param name="url">The target URL.</param>
        /// <param name="id">Optional ID for the link.</param>
        /// <returns>The ANSI escape sequence to start the hyperlink.</returns>
        public static string HyperlinkStart(string url, string id = "")
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            string parameters = string.IsNullOrEmpty(id) ? "" : $"id={id}";
            return $"{Osc}8;{parameters};{url}{Bel}";
        }

        /// <summary>
        /// Generates the OSC 8 sequence to end a hyperlink.
        /// </summary>
        /// <returns>The ANSI escape sequence to end the hyperlink.</returns>
        public static string HyperlinkEnd() => $"{Osc}8;;{St}";

        /// <summary>
        /// Gets the ANSI escape sequence for a standard System.ConsoleColor.
        /// </summary>
        /// <param name="color">The ConsoleColor to get the code for.</param>
        /// <param name="foreground">True for foreground color, false for background color.</param>
        /// <returns>The ANSI escape sequence string, or an empty string if color is null.</returns>
        public static string GetColorCode(ConsoleColor? color, bool foreground = true)
        {
            if (!color.HasValue) return string.Empty;

            return (color.Value, foreground) switch
            {
                // Foreground
                (ConsoleColor.Black,       true) => FgBlack,
                (ConsoleColor.DarkBlue,    true) => FgBlue,
                (ConsoleColor.DarkGreen,   true) => FgGreen,
                (ConsoleColor.DarkCyan,    true) => FgCyan,
                (ConsoleColor.DarkRed,     true) => FgRed,
                (ConsoleColor.DarkMagenta, true) => FgMagenta,
                (ConsoleColor.DarkYellow,  true) => FgYellow,       // Often rendered as Brown
                (ConsoleColor.Gray,        true) => FgWhite,        // Standard White
                (ConsoleColor.DarkGray,    true) => FgBrightBlack,  // Bright Black is Dark Gray
                (ConsoleColor.Blue,        true) => FgBrightBlue,
                (ConsoleColor.Green,       true) => FgBrightGreen,
                (ConsoleColor.Cyan,        true) => FgBrightCyan,
                (ConsoleColor.Red,         true) => FgBrightRed,
                (ConsoleColor.Magenta,    true) => FgBrightMagenta,
                (ConsoleColor.Yellow,      true) => FgBrightYellow,
                (ConsoleColor.White,       true) => FgBrightWhite,

                // Background
                (ConsoleColor.Black,       false) => BgBlack,
                (ConsoleColor.DarkBlue,    false) => BgBlue,
                (ConsoleColor.DarkGreen,   false) => BgGreen,
                (ConsoleColor.DarkCyan,    false) => BgCyan,
                (ConsoleColor.DarkRed,     false) => BgRed,
                (ConsoleColor.DarkMagenta, false) => BgMagenta,
                (ConsoleColor.DarkYellow,  false) => BgYellow,
                (ConsoleColor.Gray,        false) => BgWhite,
                (ConsoleColor.DarkGray,    false) => BgBrightBlack,
                (ConsoleColor.Blue,        false) => BgBrightBlue,
                (ConsoleColor.Green,       false) => BgBrightGreen,
                (ConsoleColor.Cyan,        false) => BgBrightCyan,
                (ConsoleColor.Red,         false) => BgBrightRed,
                (ConsoleColor.Magenta,    false) => BgBrightMagenta,
                (ConsoleColor.Yellow,      false) => BgBrightYellow,
                (ConsoleColor.White,       false) => BgBrightWhite,

                _ => string.Empty // Should not happen for valid ConsoleColor values
            };
        }
    }
}
