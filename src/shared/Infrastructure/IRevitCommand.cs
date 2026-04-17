namespace Bimwright.Rvt.Plugin
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
            new CommandResult { Success = false, Error = error };
    }
}
