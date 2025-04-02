using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Background_Terminal
{
    public class AnsiEscapeCodeParser
    {
        private const string ESCAPE = "\x1B";
        private static readonly Regex AnsiRegex = new Regex(@"\x1B\[(.*?)m");
        private static readonly Regex CursorPositionRegex = new Regex(@"\x1B\[(\d+)?;?(\d+)?[Hf]");
        private static readonly Regex CursorUpRegex = new Regex(@"\x1B\[(\d+)?A");
        private static readonly Regex CursorDownRegex = new Regex(@"\x1B\[(\d+)?B");
        private static readonly Regex CursorForwardRegex = new Regex(@"\x1B\[(\d+)?C");
        private static readonly Regex CursorBackwardRegex = new Regex(@"\x1B\[(\d+)?D");
        private static readonly Regex CursorHomeRegex = new Regex(@"\x1B\[H");
        private static readonly Regex CursorSavePositionRegex = new Regex(@"\x1B\[s");
        private static readonly Regex CursorRestorePositionRegex = new Regex(@"\x1B\[u");
        private static readonly Regex ClearScreenRegex = new Regex(@"\x1B\[(2J|J|0J|H\x1B\[2J|H\x1B\[J)");
        private static readonly Regex ClearToEndRegex = new Regex(@"\x1B\[0?J");
        private static readonly Regex ClearLineRegex = new Regex(@"\x1B\[(K|0K|1K|2K)");
        private static readonly Regex EraseDisplayRegex = new Regex(@"\x1B\[(\d+)?J");
        
        // Internal terminal buffer state
        private List<List<FormattedChar>> _buffer = new List<List<FormattedChar>>();
        private int _cursorRow = 0;
        private int _cursorCol = 0;
        private int _savedRow = 0;
        private int _savedCol = 0;
        private int _maxRows = 1000; // Buffer size limit
        private int _maxCols = 200;  // Max chars per line
        
        // Current text formatting state
        private SolidColorBrush _currentForeground = Brushes.White;
        private SolidColorBrush _currentBackground = Brushes.Transparent;
        private bool _isBold = false;
        private bool _isItalic = false;
        private bool _isUnderline = false;
        
        // Constructor
        public AnsiEscapeCodeParser()
        {
            _buffer.Add(new List<FormattedChar>());
        }
        
        // Reset the buffer
        public void Reset()
        {
            _buffer.Clear();
            _buffer.Add(new List<FormattedChar>());
            _cursorRow = 0;
            _cursorCol = 0;
            ResetFormatting();
        }
        
        // Reset text formatting to defaults
        private void ResetFormatting()
        {
            _currentForeground = Brushes.White;
            _currentBackground = Brushes.Transparent;
            _isBold = false;
            _isItalic = false;
            _isUnderline = false;
        }
        
        // Process incoming text with ANSI codes
        public void ProcessText(string text)
        {
            int i = 0;
            while (i < text.Length)
            {
                // Check for escape sequence
                if (i < text.Length - 1 && text[i] == '\x1B' && text[i + 1] == '[')
                {
                    int escEnd = text.IndexOf('m', i);
                    if (escEnd != -1)
                    {
                        string escSequence = text.Substring(i, escEnd - i + 1);
                        ProcessEscapeSequence(escSequence);
                        i = escEnd + 1;
                        continue;
                    }
                    
                    // Check for cursor movement
                    int endPos = FindSequenceEnd(text, i);
                    if (endPos > i)
                    {
                        string escSequence = text.Substring(i, endPos - i + 1);
                        ProcessCursorCommand(escSequence);
                        i = endPos + 1;
                        continue;
                    }
                }
                else if (text[i] == '\r')
                {
                    // Carriage return - move cursor to start of line
                    _cursorCol = 0;
                    i++;
                    continue;
                }
                else if (text[i] == '\n')
                {
                    // Line feed - move down one line
                    _cursorRow++;
                    while (_buffer.Count <= _cursorRow)
                    {
                        _buffer.Add(new List<FormattedChar>());
                    }
                    i++;
                    continue;
                }
                else
                {
                    // Regular character - add to buffer
                    AddCharToBuffer(text[i]);
                    i++;
                }
            }
        }
        
        // Find the end of an escape sequence
        private int FindSequenceEnd(string text, int start)
        {
            for (int i = start + 2; i < text.Length; i++)
            {
                char c = text[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                {
                    return i;
                }
            }
            return start;
        }
        
        // Add character at current cursor position
        private void AddCharToBuffer(char c)
        {
            EnsureBufferSize();
            
            // Ensure current row exists
            while (_buffer.Count <= _cursorRow)
            {
                _buffer.Add(new List<FormattedChar>());
            }
            
            // If cursor is beyond current line length, pad with spaces
            while (_buffer[_cursorRow].Count < _cursorCol)
            {
                _buffer[_cursorRow].Add(new FormattedChar(' ', 
                    _currentForeground, _currentBackground, 
                    _isBold, _isItalic, _isUnderline));
            }
            
            // If cursor position is within line, replace character
            if (_cursorCol < _buffer[_cursorRow].Count)
            {
                _buffer[_cursorRow][_cursorCol] = new FormattedChar(c, 
                    _currentForeground, _currentBackground, 
                    _isBold, _isItalic, _isUnderline);
            }
            else
            {
                // Otherwise add new character
                _buffer[_cursorRow].Add(new FormattedChar(c, 
                    _currentForeground, _currentBackground, 
                    _isBold, _isItalic, _isUnderline));
            }
            
            // Move cursor forward
            _cursorCol++;
        }
        
        // Ensure buffer doesn't exceed max size
        private void EnsureBufferSize()
        {
            // Limit maximum buffer size
            if (_cursorRow >= _maxRows)
            {
                int removeCount = _cursorRow - _maxRows + 10; // Keep room for more
                _buffer.RemoveRange(0, removeCount);
                _cursorRow -= removeCount;
            }
        }
        
        // Process an ANSI escape sequence for formatting
        private void ProcessEscapeSequence(string sequence)
        {
            if (sequence.EndsWith("m"))
            {
                // Get the parameters between ESC[ and m
                string paramPart = sequence.Substring(2, sequence.Length - 3);
                if (string.IsNullOrEmpty(paramPart))
                {
                    // ESC[m is a reset
                    ResetFormatting();
                    return;
                }
                
                string[] parameters = paramPart.Split(';');
                for (int i = 0; i < parameters.Length; i++)
                {
                    int code;
                    if (int.TryParse(parameters[i], out code))
                    {
                        switch (code)
                        {
                            case 0: // Reset
                                ResetFormatting();
                                break;
                            case 1: // Bold
                                _isBold = true;
                                break;
                            case 3: // Italic
                                _isItalic = true;
                                break;
                            case 4: // Underline
                                _isUnderline = true;
                                break;
                            case 22: // Bold off
                                _isBold = false;
                                break;
                            case 23: // Italic off
                                _isItalic = false;
                                break;
                            case 24: // Underline off
                                _isUnderline = false;
                                break;
                                
                            // Foreground colors (30-37)
                            case 30: _currentForeground = Brushes.Black; break;
                            case 31: _currentForeground = Brushes.Red; break;
                            case 32: _currentForeground = Brushes.Green; break;
                            case 33: _currentForeground = Brushes.Yellow; break;
                            case 34: _currentForeground = Brushes.Blue; break;
                            case 35: _currentForeground = Brushes.Magenta; break;
                            case 36: _currentForeground = Brushes.Cyan; break;
                            case 37: _currentForeground = Brushes.White; break;
                            
                            // Background colors (40-47)
                            case 40: _currentBackground = Brushes.Black; break;
                            case 41: _currentBackground = Brushes.Red; break;
                            case 42: _currentBackground = Brushes.Green; break;
                            case 43: _currentBackground = Brushes.Yellow; break;
                            case 44: _currentBackground = Brushes.Blue; break;
                            case 45: _currentBackground = Brushes.Magenta; break;
                            case 46: _currentBackground = Brushes.Cyan; break;
                            case 47: _currentBackground = Brushes.White; break;
                            
                            // Default colors
                            case 39: _currentForeground = Brushes.White; break;
                            case 49: _currentBackground = Brushes.Transparent; break;
                        }
                    }
                }
            }
        }
        
        // Process cursor movement commands
        private void ProcessCursorCommand(string sequence)
        {
            // Cursor home - ESC[H
            if (CursorHomeRegex.IsMatch(sequence))
            {
                _cursorRow = 0;
                _cursorCol = 0;
                return;
            }
            
            // Position cursor - ESC[<row>;<col>H or ESC[<row>;<col>f
            Match posMatch = CursorPositionRegex.Match(sequence);
            if (posMatch.Success)
            {
                int row = 1;  // Default to 1 (first row)
                int col = 1;  // Default to 1 (first column)
                
                // Parse row if present
                if (posMatch.Groups[1].Success && !string.IsNullOrEmpty(posMatch.Groups[1].Value))
                {
                    row = int.Parse(posMatch.Groups[1].Value);
                }
                
                // Parse column if present
                if (posMatch.Groups[2].Success && !string.IsNullOrEmpty(posMatch.Groups[2].Value))
                {
                    col = int.Parse(posMatch.Groups[2].Value);
                }
                
                // Convert to 0-based and set
                _cursorRow = Math.Max(0, row - 1);  // ANSI is 1-based
                _cursorCol = Math.Max(0, col - 1);
                
                // Ensure we have enough rows
                EnsureBufferSize();
                while (_buffer.Count <= _cursorRow)
                {
                    _buffer.Add(new List<FormattedChar>());
                }
                return;
            }
            
            // Save cursor position - ESC[s
            if (CursorSavePositionRegex.IsMatch(sequence))
            {
                _savedRow = _cursorRow;
                _savedCol = _cursorCol;
                return;
            }
            
            // Restore cursor position - ESC[u
            if (CursorRestorePositionRegex.IsMatch(sequence))
            {
                _cursorRow = _savedRow;
                _cursorCol = _savedCol;
                
                // Ensure we have enough rows
                while (_buffer.Count <= _cursorRow)
                {
                    _buffer.Add(new List<FormattedChar>());
                }
                return;
            }
            
            // Cursor up - ESC[<n>A
            Match upMatch = CursorUpRegex.Match(sequence);
            if (upMatch.Success)
            {
                int count = 1; // Default if no number specified
                if (upMatch.Groups[1].Success && !string.IsNullOrEmpty(upMatch.Groups[1].Value))
                {
                    count = int.Parse(upMatch.Groups[1].Value);
                }
                _cursorRow = Math.Max(0, _cursorRow - count);
                return;
            }
            
            // Cursor down - ESC[<n>B
            Match downMatch = CursorDownRegex.Match(sequence);
            if (downMatch.Success)
            {
                int count = 1; // Default if no number specified
                if (downMatch.Groups[1].Success && !string.IsNullOrEmpty(downMatch.Groups[1].Value))
                {
                    count = int.Parse(downMatch.Groups[1].Value);
                }
                _cursorRow += count;
                
                // Ensure we have enough rows
                while (_buffer.Count <= _cursorRow)
                {
                    _buffer.Add(new List<FormattedChar>());
                }
                return;
            }
            
            // Cursor forward - ESC[<n>C
            Match forwardMatch = CursorForwardRegex.Match(sequence);
            if (forwardMatch.Success)
            {
                int count = 1; // Default if no number specified
                if (forwardMatch.Groups[1].Success && !string.IsNullOrEmpty(forwardMatch.Groups[1].Value))
                {
                    count = int.Parse(forwardMatch.Groups[1].Value);
                }
                _cursorCol += count;
                return;
            }
            
            // Cursor backward - ESC[<n>D
            Match backwardMatch = CursorBackwardRegex.Match(sequence);
            if (backwardMatch.Success)
            {
                int count = 1; // Default if no number specified
                if (backwardMatch.Groups[1].Success && !string.IsNullOrEmpty(backwardMatch.Groups[1].Value))
                {
                    count = int.Parse(backwardMatch.Groups[1].Value);
                }
                _cursorCol = Math.Max(0, _cursorCol - count);
                return;
            }
            
            // Clear screen - ESC[2J, ESC[J, etc.
            Match clearScreenMatch = ClearScreenRegex.Match(sequence);
            if (clearScreenMatch.Success)
            {
                _buffer.Clear();
                _buffer.Add(new List<FormattedChar>());
                _cursorRow = 0;
                _cursorCol = 0;
                return;
            }
            
            // Erase display - ESC[nJ
            Match eraseDisplayMatch = EraseDisplayRegex.Match(sequence);
            if (eraseDisplayMatch.Success)
            {
                string param = "0"; // Default is 0 - clear from cursor to end of screen
                if (eraseDisplayMatch.Groups[1].Success && !string.IsNullOrEmpty(eraseDisplayMatch.Groups[1].Value))
                {
                    param = eraseDisplayMatch.Groups[1].Value;
                }
                
                if (param == "0" || param == "")
                {
                    // Clear from cursor to end of screen
                    if (_cursorRow < _buffer.Count)
                    {
                        // Clear rest of current line
                        if (_cursorCol < _buffer[_cursorRow].Count)
                        {
                            _buffer[_cursorRow].RemoveRange(_cursorCol, _buffer[_cursorRow].Count - _cursorCol);
                        }
                        
                        // Clear all lines below
                        if (_cursorRow < _buffer.Count - 1)
                        {
                            _buffer.RemoveRange(_cursorRow + 1, _buffer.Count - _cursorRow - 1);
                        }
                    }
                }
                else if (param == "1")
                {
                    // Clear from beginning of screen to cursor
                    for (int i = 0; i < _cursorRow; i++)
                    {
                        _buffer[i].Clear();
                    }
                    
                    // Clear current line up to cursor
                    if (_cursorRow < _buffer.Count && _cursorCol < _buffer[_cursorRow].Count)
                    {
                        _buffer[_cursorRow].RemoveRange(0, Math.Min(_cursorCol + 1, _buffer[_cursorRow].Count));
                    }
                }
                else if (param == "2")
                {
                    // Clear entire screen
                    _buffer.Clear();
                    _buffer.Add(new List<FormattedChar>());
                    
                    // Some terminals move cursor to home, others leave it in place
                    // We'll move it to home for better compatibility
                    _cursorRow = 0;
                    _cursorCol = 0;
                }
                return;
            }
            
            // Clear line - ESC[K or ESC[0K (clear from cursor to end)
            // ESC[1K (clear from start to cursor), ESC[2K (clear entire line)
            Match clearLineMatch = ClearLineRegex.Match(sequence);
            if (clearLineMatch.Success)
            {
                string param = clearLineMatch.Groups[1].Value;
                
                if (param == "K" || param == "0K" || param == "")
                {
                    // Clear from cursor to end of line
                    if (_cursorRow < _buffer.Count && _cursorCol < _buffer[_cursorRow].Count)
                    {
                        _buffer[_cursorRow].RemoveRange(_cursorCol, _buffer[_cursorRow].Count - _cursorCol);
                    }
                }
                else if (param == "1K")
                {
                    // Clear from start to cursor
                    if (_cursorRow < _buffer.Count && _cursorCol > 0)
                    {
                        int removeCount = Math.Min(_cursorCol, _buffer[_cursorRow].Count);
                        if (removeCount > 0)
                        {
                            _buffer[_cursorRow].RemoveRange(0, removeCount);
                        }
                    }
                }
                else if (param == "2K")
                {
                    // Clear entire line
                    if (_cursorRow < _buffer.Count)
                    {
                        _buffer[_cursorRow].Clear();
                    }
                }
                return;
            }
        }
        
        // Render buffer to FlowDocument for RichTextBox
        public FlowDocument RenderToFlowDocument()
        {
            FlowDocument document = new FlowDocument();
            Paragraph paragraph = new Paragraph();
            
            // Track current formatting state to minimize Run objects
            Brush currentFormatFg = null;
            Brush currentFormatBg = null;
            bool currentFormatIsBold = false;
            bool currentFormatIsItalic = false;
            bool currentFormatIsUnderline = false;
            
            StringBuilder currentRunText = new StringBuilder(1000);
            
            for (int row = 0; row < _buffer.Count; row++)
            {
                if (row > 0)
                {
                    // Flush any accumulated text before the line break
                    if (currentRunText.Length > 0)
                    {
                        Run run = new Run(currentRunText.ToString());
                        ApplyFormatting(run, currentFormatFg, currentFormatBg, 
                                      currentFormatIsBold, currentFormatIsItalic, currentFormatIsUnderline);
                        paragraph.Inlines.Add(run);
                        currentRunText.Clear();
                    }
                    
                    paragraph.Inlines.Add(new LineBreak());
                }
                
                for (int col = 0; col < _buffer[row].Count; col++)
                {
                    FormattedChar fc = _buffer[row][col];
                    
                    // Check if formatting has changed
                    if (fc.Foreground != currentFormatFg || 
                        fc.Background != currentFormatBg ||
                        fc.IsBold != currentFormatIsBold ||
                        fc.IsItalic != currentFormatIsItalic ||
                        fc.IsUnderline != currentFormatIsUnderline)
                    {
                        // Flush accumulated text with the old formatting
                        if (currentRunText.Length > 0)
                        {
                            Run run = new Run(currentRunText.ToString());
                            ApplyFormatting(run, currentFormatFg, currentFormatBg, 
                                          currentFormatIsBold, currentFormatIsItalic, currentFormatIsUnderline);
                            paragraph.Inlines.Add(run);
                            currentRunText.Clear();
                        }
                        
                        // Update current formatting
                        currentFormatFg = fc.Foreground;
                        currentFormatBg = fc.Background;
                        currentFormatIsBold = fc.IsBold;
                        currentFormatIsItalic = fc.IsItalic;
                        currentFormatIsUnderline = fc.IsUnderline;
                    }
                    
                    // Add to current run
                    currentRunText.Append(fc.Character);
                }
            }
            
            // Flush any remaining text
            if (currentRunText.Length > 0)
            {
                Run run = new Run(currentRunText.ToString());
                ApplyFormatting(run, currentFormatFg, currentFormatBg, 
                              currentFormatIsBold, currentFormatIsItalic, currentFormatIsUnderline);
                paragraph.Inlines.Add(run);
            }
            
            document.Blocks.Add(paragraph);
            return document;
        }
        
        // Helper method to apply formatting to a Run
        private void ApplyFormatting(Run run, Brush foreground, Brush background, 
                                bool isBold, bool isItalic, bool isUnderline)
        {
            if (foreground != null)
                run.Foreground = foreground;
                
            if (background != null)
                run.Background = background;
                
            if (isBold)
                run.FontWeight = FontWeights.Bold;
                
            if (isItalic)
                run.FontStyle = FontStyles.Italic;
                
            if (isUnderline)
                run.TextDecorations = TextDecorations.Underline;
        }
        
        // Inner class to store formatted characters
        private class FormattedChar
        {
            public char Character { get; set; }
            public Brush Foreground { get; set; }
            public Brush Background { get; set; }
            public bool IsBold { get; set; }
            public bool IsItalic { get; set; }
            public bool IsUnderline { get; set; }
            
            public FormattedChar(char character, Brush foreground, Brush background, 
                bool isBold, bool isItalic, bool isUnderline)
            {
                Character = character;
                Foreground = foreground;
                Background = background;
                IsBold = isBold;
                IsItalic = isItalic;
                IsUnderline = isUnderline;
            }
        }
    }
}