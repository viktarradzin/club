using WebApi.Services.Enrollments;
using WebApi.Services.Waitlist;
using WebApi.Services.Caching;
using WebApi.Services.Transactions;
using System.Text;
using Common.Caching;
using Common.RateLimiting;
using DataAccessLayer;
using DataAccessLayer.Entities;
using DataAccessLayer.Seeding;
using DataAccessLayer.UnitOfWork;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using StackExchange.Redis;
using WebApi.Auth;
using WebApi.Middleware;
using WebApi.Services;
using WebApi.Services.Analytics;
using WebApi.Services.Schedule;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

builder.Services.AddControllers(options =>
{
    options.AllowEmptyInputInBodyModelBinding = true;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Enter JWT token"
    });

    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

builder.Services.AddDbContext<ClubDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<IEnrollmentPreparationService, EnrollmentPreparationService>();
builder.Services.AddScoped<IEnrollmentIdempotencyService, EnrollmentIdempotencyService>();
builder.Services.AddScoped<IWaitlistIdempotencyService, WaitlistIdempotencyService>();
builder.Services.AddScoped<ITransactionExecutor, TransactionExecutor>();
builder.Services.AddScoped<IEnrollmentService, EnrollmentService>();
builder.Services.AddScoped<IWaitlistService, WaitlistService>();
builder.Services.AddScoped<IWaitlistPromotionService, WaitlistPromotionService>();
builder.Services.AddScoped<ICardCacheVersionService, CardCacheVersionService>();
builder.Services.AddScoped<IScheduleService, ScheduleService>();
builder.Services.AddScoped<IAnalyticsService, AnalyticsService>();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Redis")!));

builder.Services.AddSingleton<ICacheService>(provider =>
    new RedisCacheService(provider.GetRequiredService<IConnectionMultiplexer>(), TimeSpan.FromMinutes(5)));
builder.Services.AddSingleton<IUserRateLimiter, RedisUserRateLimiter>();

builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

var jwtOptions = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()!;

builder.Services.AddSingleton(jwtOptions);
builder.Services.AddSingleton<AccessTokenService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key)),
            ClockSkew = TimeSpan.Zero
        };
    });

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();

app.UseSwagger();
app.UseSwaggerUI();

app.UseRouting();
app.UseAuthentication();

app.UseMiddleware<UserRateLimitMiddleware>();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health");

app.Services.GetRequiredService<IConnectionMultiplexer>();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<ClubDbContext>();

    await dbContext.Database.MigrateAsync();
    await DatabaseSeeder.SeedAsync(dbContext);
}

app.Run();

public partial class Program;
