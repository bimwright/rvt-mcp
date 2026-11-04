namespace RvtMcp.Plugin
{
    public interface IRevitCommand
    {
        string Name { get; }
        string Description { get; }
        string ParametersSchema { get; }
        CommandResult Execute(Autodesk.Revit.UI.UIApplication app, string paramsJson);
    }

    public class CommandResult
    {
        public bool Success { get; set; }
        public object Data { get; set; }
        public string Error { get; set; }

        public static CommandResult Ok(object data) =>
            new CommandResult { Success = true, Data = data };

        public static CommandResult Fail(string error) =>
            new CommandResult
            {
                Success = false, Error = error,
                Data = error == "FILE_CHANGED_ON_DISK" ? new
                {
                    next_step = "Disk and memory save counts differ. If this followed Reload Latest in Revit UI on a workshared local, ask the user to review and Save the local in Revit (not Synchronize), then read document state again before retrying. Otherwise inspect possible external file replacement; do not overwrite or retry automatically."
                } : null
            };
    }
}
