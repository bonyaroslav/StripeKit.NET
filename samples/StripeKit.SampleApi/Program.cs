var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Stripe webhook endpoint placeholder.
// IMPORTANT: real implementation must verify Stripe signature using the RAW request body.
app.MapPost("/webhooks/stripe", () => Results.Ok());

// Reconciliation endpoint placeholder (demo-only; extractable to HostedService/CLI later).
app.MapPost("/reconcile", () => Results.Ok());

app.Run();
