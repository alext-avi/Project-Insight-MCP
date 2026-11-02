using ProjectInsightMCP.Web;

var builder = WebApplication.CreateBuilder(args);
builder.AddProjectInsightWeb();

var app = builder.Build();
app.UseProjectInsightWeb();
app.Run();

/// <summary>Entry point; partial so tests can host it with WebApplicationFactory.</summary>
public partial class Program;
