﻿using AspNetCoreRateLimit;
using BackEnd_Exp.Attributes;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MySql.Data.MySqlClient;
using NrExtras.EncryptionHelper;
using NrExtras.Logger;
using System.Text;
using static WebApi.ConfigClassesDefinitions;

namespace WebApi
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        //configure services
        public void ConfigureServices(IServiceCollection services)
        {
            try
            {
                #region Db
                string connectionString;

                //set db service
                if (Configuration.GetValue<bool>("DbIsSQLLite"))
                {
                    Logger.WriteToLog("Using SqlLite db");
                    connectionString = Configuration.GetConnectionString("DefaultConnection");

                    // Add SQL Lite DB services
                    services.AddDbContext<AppDbContext>(options =>
                        options.UseSqlite(connectionString));
                }
                else
                {
                    Logger.WriteToLog("Using MySQL db");
                    //get connection string based on develop or production env
                    try
                    {
                        string pass;
                        if (GlobalDynamicSettings.DebugMode_RunningLocal)
                            pass = Configuration.GetValue<string>("Db_MySQL:sql_Pass_enc:local");
                        else
                            pass = Configuration.GetValue<string>("Db_MySQL:sql_Pass_enc:production");

                        //pattern - server=localhost;user=root;password=;database=uni_db
                        connectionString = $"server={Configuration.GetValue<string>("Db_MySQL:server")};user={Configuration.GetValue<string>("Db_MySQL:sql_User")};password={EncryptionHelper.DecryptKey(pass)};database={Configuration.GetValue<string>("Db_MySQL:database")}";
                    }
                    catch
                    {
                        Logger.WriteToLog("Error getting MySql connection string", Logger.LogLevel.Error);
                        throw;
                    }

                    // Configure the MySQL database context
                    services.AddDbContext<AppDbContext>(options =>
                        options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));
                }

                // Create the Users table if it doesn't exist
                EnsureTablesCreated(Configuration.GetValue<bool>("DbIsSQLLite"), connectionString);
                #endregion
                #region Add JWT Bearer Authentication
                // Bind JWT configuration from appsettings.json
                JwtConfig jwtConfig = Configuration.GetSection("JWT").Get<JwtConfig>();
                services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                    .AddJwtBearer(options =>
                    {
                        options.TokenValidationParameters = new TokenValidationParameters
                        {
                            ValidateIssuer = true,
                            ValidateAudience = true,
                            ValidateLifetime = true,
                            ValidateIssuerSigningKey = true,
                            ValidIssuer = jwtConfig.Issuer,
                            ValidAudience = jwtConfig.Audience,
                            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(EncryptionHelper.DecryptKey(GlobalDynamicSettings.JwtToken_HashedSecnret)))
                        };
                    });
                #endregion
                services.AddControllers();

                #region Cleanup services
                // Add hosted service for expired sessions cleanup
                services.AddHostedService<ExpiredSessionsCleanupService>();
                // Add the new service for cleaning up unconfirmed emails
                services.AddHostedService<UnconfirmedEmailsCleanupService>();
                #endregion

                //user related operation service
                services.AddScoped<UserService>();
                //add local only attribute
                services.AddScoped<LocalOnlyAttribute>();
                #region Cors
                //add cors
                ////Allow all cors - use only for develop environment
                //services.AddCors(options =>
                //{
                //    options.AddPolicy("AllowGetForAll",
                //        builder => builder.AllowAnyOrigin()
                //        .AllowAnyHeader().WithMethods("GET"));
                //});

                string[] corsAllowedAddress;
                //adding specific address for cors to allow them to connect - local or production
                //if (bool.Parse(ConfigurationHelper.GetConfig()["DebugMode_RunningLocal"]) == true)
                if (GlobalDynamicSettings.DebugMode_RunningLocal)
                    corsAllowedAddress = Configuration.GetValue<string>("Cors:local").Split(";");
                else
                    corsAllowedAddress = Configuration.GetValue<string>("Cors:production").Split(";");

                //setting cors for each address
                foreach (string address in corsAllowedAddress)
                    Logger.WriteToLog("Cors allowed address: " + address);

                //add cors service
                services.AddCors(options =>
                {
                    options.AddPolicy("Cors_AllowOrigin_SpecificAddress",
                    builder => builder.WithOrigins(corsAllowedAddress).AllowAnyHeader().AllowAnyMethod().AllowCredentials());
                });
                #endregion
                #region Configure rate limiting
                services.AddOptions();
                services.AddMemoryCache();
                services.Configure<IpRateLimitOptions>(Configuration.GetSection("IpRateLimiting"));
                services.Configure<IpRateLimitPolicies>(Configuration.GetSection("IpRateLimitPolicies"));
                services.AddInMemoryRateLimiting();  // Use distributed rate limiting if needed
                services.AddSingleton<IRateLimitConfiguration, RateLimitConfiguration>();
                #endregion
            }
            catch
            {
                throw;
            }
        }

        //cofigure app
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            if (env.IsDevelopment())
                app.UseDeveloperExceptionPage();
            else
            {
                // Production error handling
                app.UseExceptionHandler("/Home/Error");
                app.UseHsts();
            }

            //setting UseIpRateLimiting
            app.UseIpRateLimiting();

            // Enable authentication
            app.UseAuthentication();

            app.UseHttpsRedirection();
            app.UseRouting();

            // Enable CORS
            app.UseCors("Cors_AllowOrigin_SpecificAddress");

            // Enable authorization
            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
            });
        }

        //prepare db if not exists
        private void EnsureTablesCreated(bool isSqlLite, string connectionString)
        {
            if (isSqlLite)
            {//sql lite
                using (var connection = new SqliteConnection(connectionString))
                {
                    connection.Open();

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Users (
                    Id TEXT PRIMARY KEY,
                    UserName TEXT NOT NULL,
                    Email TEXT NOT NULL,
                    EmailConfirmed INTEGER NOT NULL,
                    Password TEXT NOT NULL,
                    RegistrationDate DATETIME DEFAULT CURRENT_TIMESTAMP
                    )";
                        command.ExecuteNonQuery();

                        command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS ActiveSessions (
                    Id TEXT PRIMARY KEY,
                    Token TEXT NOT NULL,
                    UserId TEXT NOT NULL,
                    SignInDate DATETIME DEFAULT CURRENT_TIMESTAMP
                    )"
                        ;
                        command.ExecuteNonQuery();
                    }
                }
            }
            else
            {// MySQL
                using (var connection = new MySqlConnection(connectionString))
                {
                    connection.Open();

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Users (
                    Id VARCHAR(255) PRIMARY KEY,
                    UserName VARCHAR(255) NOT NULL,
                    Email VARCHAR(255) NOT NULL,
                    EmailConfirmed TINYINT(1) NOT NULL,
                    Password TEXT NOT NULL,
                    RegistrationDate DATETIME DEFAULT CURRENT_TIMESTAMP
                    )";
                        command.ExecuteNonQuery();

                        command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS ActiveSessions (
                    Id VARCHAR(255) PRIMARY KEY,
                    Token TEXT NOT NULL,
                    UserId VARCHAR(255) NOT NULL,
                    SignInDate DATETIME DEFAULT CURRENT_TIMESTAMP
                    )";
                        command.ExecuteNonQuery();
                    }
                }
            }
        }
    }
}