using RvtMcp.Plugin.Reports.Model;

namespace RvtMcp.Plugin.Reports.Binding
{
    public interface IReportResolver
    {
        ReportDocument ActiveDocument { get; }
        ReportDocument FindDocument(string token);
        ReportElement FindElement(string token, long id);
        ReportView FindView(string token, long id);
    }
}
