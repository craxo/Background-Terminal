namespace Background_Terminal.DTOs
{
    public static class TerminalCommandRegistry
    {
        public static List<string> ClearCommands { get; } = new List<string>
        {
            "clear",    // Unit/Linux style
            "cls"  // Windows Command Prompt
        };

        public static bool IsClearCommand(string command)
        {
            return ClearCommands.Contains(command.Trim().ToLower());
        }

        public static List<string> HelpCommands { get; } = new List<string>
        {
            "help",
            "?",
            "h"
        };

        public static bool IsHelpCommand(string command)
        {
            return HelpCommands.Contains(command.Trim().ToLower());
        }

        public static List<string> BgtCustomCommands { get; } = new()
        {
            "stop",
            "quit",
            "help"
        };

    }
}
