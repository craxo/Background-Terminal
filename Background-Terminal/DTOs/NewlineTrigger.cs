using System.ComponentModel.DataAnnotations;

namespace Background_Terminal.DTOs
{
    public class NewlineTrigger
    {
        [Required]
        public string TriggerCommand { get; set; }

        [Required]
        public string ExitCommand { get; set; }

        [Required]
        public string NewlineString { get; set; }

        public NewlineTrigger() { }

        public NewlineTrigger(string triggerCommand, string exitCommand, string newlineString)
        {
            TriggerCommand = triggerCommand;
            ExitCommand = exitCommand;
            NewlineString = newlineString;
        }
    }
}