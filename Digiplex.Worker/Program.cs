using Digiplex.Core.Models;
using Digiplex.Core.Protocol;
using Digiplex.Core.Services;
using Digiplex.Mqtt;
using Digiplex.Serial;
using Digiplex.Worker;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSerilog(config =>
  config.ReadFrom.Configuration(builder.Configuration));

// Configuration
builder.Services.Configure<SerialOptions>(builder.Configuration.GetSection("Serial"));
builder.Services.Configure<PartitionConfig>(builder.Configuration.GetSection("Partitions"));
builder.Services.Configure<MqttOptions>(builder.Configuration.GetSection("Mqtt"));

// Core services
builder.Services.AddSingleton<IDigiplexCodec, DigiplexCodec>();
builder.Services.AddSingleton<DigiplexState>();
builder.Services.AddSingleton<IDigiplexState>(sp => sp.GetRequiredService<DigiplexState>());

// Serial service (real or mock based on configuration)
if (builder.Configuration.GetSection("Serial").GetValue<bool>("UseMock"))
  builder.Services.AddSingleton<ISerialService, MockSerialService>();
else
  builder.Services.AddSingleton<ISerialService, SerialService>();

// MQTT service
builder.Services.AddSingleton<IMqttService, MqttService>();

// Background worker
builder.Services.AddHostedService<DigiplexWorker>();

var host = builder.Build();
host.Run();
