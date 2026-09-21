using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using ChatApp.Infrastructure.Data;
using ChatApp.Infrastructure.Services;
using ChatApp.API.Hubs;
using ChatApp.API.Middleware;
using ChatApp.Infrastructure.Repositories;
using ChatApp.Application.Services;
using ChatApp.Application.Interfaces;
using ChatApp.Infrastructure.Options;
using ChatApp.Infrastructure.Push;
using ChatApp.Infrastructure.Email;
using ChatApp.Infrastructure.Email.Background;

AppContext.SetSwitch("System.Net.DisableIPv6", true);

var builder = WebApplication.CreateBuilder(args);

// ============ DATABASE ============
builder.Services.AddDbContext<ChatDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        b => b.MigrationsAssembly("ChatApp.API")
    )
);

// ============ JWT AUTHENTICATION ============
var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var secretKey = jwtSettings["SecretKey"];
if (string.IsNullOrWhiteSpace(secretKey) || secretKey.Length < 32)
{
    throw new InvalidOperationException(
        "JwtSettings:SecretKey must be at least 32 characters. " +
        "Set it in appsettings.Development.json, User Secrets, or environment variable JwtSettings__SecretKey");
}

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSettings["Issuer"],
        ValidAudience = jwtSettings["Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secretKey))
    };

    // Allow JWT in SignalR connections
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/chatHub"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        }
    };
});

// ============ SIGNALR ============
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
});

// ============ CORS ============
var configuredCors = builder.Configuration["Cors:AllowedOrigins"]?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    ?? new[] { "http://localhost:3000", "http://localhost:5173", "http://localhost:5174", "https://localhost:5174" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReact", policy =>
    {
        policy.WithOrigins(configuredCors)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Important for SignalR!
    });
});

// ============ REGISTER REPOSITORIES ============
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IMessageRepository, MessageRepository>();
builder.Services.AddScoped<IConversationRepository, ConversationRepository>();
builder.Services.AddScoped<IUserConnectionRepository, UserConnectionRepository>();

// ============ REGISTER SERVICES ============
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IMessageService, MessageService>();
builder.Services.AddScoped<IConversationService, ConversationService>();
builder.Services.AddScoped<IPresenceService, PresenceService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IUserDeviceRepository, ChatApp.Infrastructure.Repositories.UserDeviceRepository>();
builder.Services.AddScoped<ISupportTicketRepository, SupportTicketRepository>();
builder.Services.AddScoped<IEmailOutboxRepository, EmailOutboxRepository>();
builder.Services.AddScoped<IOtpRepository, OtpRepository>();

builder.Services.AddScoped<ISupportTicketService, SupportTicketService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IOtpService, OtpService>();
builder.Services.AddSingleton<IPasswordValidator, PasswordValidator>();
builder.Services.AddScoped<IPasswordRecoveryService, PasswordRecoveryService>();

// ============ AI CUSTOMER SUPPORT CHATBOT SERVICES ============
// VectorSidecarKBService delegates semantic KB search to the Python FAISS sidecar (localhost:8001).
// Falls back automatically to inline keyword search if the sidecar is unreachable.
// AddHttpClient manages the HttpClient lifetime (connection pooling, no socket exhaustion).
builder.Services.AddHttpClient<ChatApp.API.Services.IKnowledgeBaseService, ChatApp.API.Services.VectorSidecarKBService>();
builder.Services.AddScoped<ChatApp.API.Services.IEmailService, ChatApp.API.Services.EmailService>();
builder.Services.AddScoped<ChatApp.API.Services.ITicketService, ChatApp.API.Services.TicketService>();
builder.Services.AddScoped<ChatApp.API.Services.IGeminiService, ChatApp.API.Services.GeminiService>();

// Phase 2 — AI Orchestration Services
// Intent router — fast lightweight Gemini call, scoped per-request
builder.Services.AddScoped<ChatApp.API.Services.IIntentService, ChatApp.API.Services.IntentService>();
// Memory service — reads/writes session working memory and history summaries
builder.Services.AddScoped<ChatApp.API.Services.IMemoryService, ChatApp.API.Services.MemoryService>();
// Scope policy — deterministic OutOfScope guard enforcement
builder.Services.AddSingleton<ChatApp.API.Services.IChatScopePolicy, ChatApp.API.Services.ChatScopePolicy>();


// ============ OTP SETTINGS ============
builder.Services.Configure<OtpSettings>(builder.Configuration.GetSection("OtpSettings"));

// ============ EMAIL & OUTBOX ============
builder.Services.Configure<EmailSettings>(builder.Configuration.GetSection("EmailSettings"));
builder.Services.AddSingleton<IEmailTemplateRenderer, EmailTemplateRenderer>();

var emailProvider = builder.Configuration.GetSection("EmailSettings")["Provider"] ?? "DevConsole";
if (emailProvider.Equals("Smtp", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddTransient<IEmailProvider, SmtpEmailProvider>();
}
else
{
    builder.Services.AddTransient<IEmailProvider, DevConsoleEmailProvider>();
}

builder.Services.AddHostedService<EmailQueueWorker>();

builder.Services.Configure<PushSettings>(builder.Configuration.GetSection("PushSettings"));
builder.Services.AddSingleton<IPushProvider, FcmPushProvider>();
// builder.Services.AddHostedService<NotificationWorker>(); // TODO: create NotificationWorker

// ============ CONTROLLERS & SWAGGER ============
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "ChatApp API",
        Version = "v1",
        Description = "Real-time chat application API with SignalR"
    });

    // Add JWT Authentication to Swagger
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token",
        Name = "Authorization",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        {
            new Microsoft.OpenApi.Models.OpenApiSecurityScheme
            {
                Reference = new Microsoft.OpenApi.Models.OpenApiReference
                {
                    Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            new string[] {}
        }
    });
});

var app = builder.Build();

// ============ MIDDLEWARE ============
app.UseMiddleware<ExceptionHandlerMiddleware>();
app.UseMiddleware<ChatApp.API.Middleware.ChatRateLimitingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "ChatApp API v1");
    });
}
else
{
    app.UseHttpsRedirection();
}
app.UseCors("AllowReact");

var uploadsPath = Path.Combine(builder.Environment.ContentRootPath, "wwwroot", "uploads");
if (!Directory.Exists(uploadsPath))
{
    Directory.CreateDirectory(uploadsPath);
}
app.UseStaticFiles();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsPath),
    RequestPath = "/uploads"
});

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// ============ SIGNALR ENDPOINT ============
app.MapHub<ChatHub>("/chatHub");

// ============ ROOT & HEALTH ENDPOINTS ============
// Redirect root URL to Swagger documentation in a browser tab
app.MapGet("/", () => Results.Redirect("/swagger"));

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    timestamp = DateTime.UtcNow,
    message = "ChatApp API is running!"
}));

// ============ RESET ORPHANED CONNECTIONS ON STARTUP ============
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
    try
    {
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE UserConnections SET IsActive = 0, DisconnectedAt = GETUTCDATE() WHERE IsActive = 1");
        await dbContext.Database.ExecuteSqlRawAsync("UPDATE Users SET Status = 0 WHERE Status = 1");

        // EF Core warm-up: compile the model, expression tree, and prime connection pool
        // so the FIRST user request after restart is fast, not slow
        _ = await dbContext.Users.AsNoTracking().Take(1).CountAsync();
        _ = await dbContext.Conversations.AsNoTracking().Take(1).CountAsync();
        _ = await dbContext.Messages.AsNoTracking().Take(1).CountAsync();

        var dummyGuid = Guid.Empty;
        _ = await dbContext.Conversations
            .AsNoTracking()
            .AsSplitQuery()
            .Include(c => c.Participants).ThenInclude(p => p.User)
            .Include(c => c.Messages.OrderByDescending(m => m.SentAt).Take(1))
            .Where(c => c.Participants.Any(p => p.UserId == dummyGuid && p.LeftAt == null))
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ToListAsync();

        _ = await dbContext.ConversationParticipants
            .AsNoTracking()
            .AnyAsync(p => p.ConversationId == dummyGuid && p.UserId == dummyGuid && p.LeftAt == null);

        _ = await dbContext.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == dummyGuid)
            .Include(m => m.Sender)
            .Include(m => m.ReplyToMessage).ThenInclude(rm => rm.Sender)
            .Include(m => m.Reactions).ThenInclude(r => r.User)
            .OrderByDescending(m => m.SentAt)
            .Take(1)
            .ToListAsync();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogWarning(ex, "Could not reset stale user connections on startup.");
    }
}

app.Run();
