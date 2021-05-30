using System.Collections.Generic;
using System.Linq;
using Amazon.SQS;
using newkilibraries;
using Microsoft.Extensions.Configuration;
using newki_inventory_pallet.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Net.Http;
using System.Text;
using Amazon;
using System;
using RabbitMQ.Client.Events;
using RabbitMQ.Client;
using System.Threading;

namespace newki_inventory_pallet
{
    class Program
    {
        static ManualResetEvent _quitEvent = new ManualResetEvent(false);
        private static ServiceProvider serviceProvider;
        private static string _connectionString;
        private static IAwsService awsService;

        static void Main(string[] args)
        {

            //Reading configuration
            var pallets = new List<Pallet>();
            var awsStorageConfig = new AwsStorageConfig();
            var builder = new ConfigurationBuilder()
            .AddJsonFile($"appsettings.json", true, true);
            var Configuration = builder.Build();

            Configuration.GetSection("AwsStorageConfig").Bind(awsStorageConfig);
            _connectionString = Configuration.GetConnectionString("DefaultConnection");

            var services = new ServiceCollection();
            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(Configuration.GetConnectionString("DefaultConnection")));
            services.AddTransient<IAwsService, AwsService>();
            services.AddTransient<IPalletService, PalletService>();
            services.AddSingleton<IAwsStorageConfig>(awsStorageConfig);

            var serviceProvider = services.BuildServiceProvider();
            awsService = serviceProvider.GetService<IAwsService>();

            var requestQueueName = "PalletRequest";
            var responseQueueName = "PalletResponse";

            ConnectionFactory factory = new ConnectionFactory();
            factory.UserName = "user";
            factory.Password = "password";
            factory.HostName = "localhost";

            var connection = factory.CreateConnection();

            var channel = connection.CreateModel();
            channel.QueueDeclare(requestQueueName, false, false, false);
            var consumer = new EventingBasicConsumer(channel);
            consumer.Received += (ch, ea) =>
            {
                var content = Encoding.UTF8.GetString(ea.Body.ToArray());
                var updatePalletFullNameModel = JsonSerializer.Deserialize<InventoryMessage>(content);

                ProcessRequest(updatePalletFullNameModel);

            }; ;
            channel.BasicConsume(queue: requestQueueName,
                   autoAck: true,
                   consumer: consumer);


            _quitEvent.WaitOne();

        }

        private static void ProcessRequest(InventoryMessage inventoryMessage)
        {
            try
            {
                var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
                optionsBuilder.UseNpgsql(_connectionString);

                using (var appDbContext = new ApplicationDbContext(optionsBuilder.Options))
                {
                    var palletService = new PalletService(appDbContext, awsService);
                    var messageType = Enum.Parse<InventoryMessageType>(inventoryMessage.Command);

                    switch (messageType)
                    {
                        case InventoryMessageType.Search:
                            {
                                Console.WriteLine("Loading all the Pallets...");
                                var pallets = appDbContext.Pallet.OrderByDescending(p => p.PalletId).ToList();

                                foreach (var pallet in pallets)
                                {
                                    var existingPallet = appDbContext.PalletDataView.Find(pallet.PalletId);
                                    if (existingPallet != null)
                                    {

                                        existingPallet.Data = JsonSerializer.Serialize(pallet);
                                    }
                                    else
                                    {
                                        var PalletDataView = new PalletDataView
                                        {
                                            PalletId = pallet.PalletId,
                                            Data = JsonSerializer.Serialize(pallet)
                                        };
                                        appDbContext.PalletDataView.Add(PalletDataView);
                                    }
                                    appDbContext.SaveChanges();
                                }
                                palletService.UpdateFilters();
                                break;
                            }
                        case InventoryMessageType.Get:
                            {
                                Console.WriteLine("Loading a pallet...");
                                var id = JsonSerializer.Deserialize<int>(inventoryMessage.Message);
                                var pallet = palletService.GetPallet(id);
                                var content = JsonSerializer.Serialize(pallet);

                                var responseMessageNotification = new InventoryMessage();
                                responseMessageNotification.Command = InventoryMessageType.Get.ToString();
                                responseMessageNotification.RequestNumber = inventoryMessage.RequestNumber;
                                responseMessageNotification.MessageDate = DateTimeOffset.UtcNow;

                                var inventoryResponseMessage = new InventoryMessage();
                                inventoryResponseMessage.Message = content;
                                inventoryResponseMessage.Command = inventoryMessage.Command;
                                inventoryResponseMessage.RequestNumber = inventoryMessage.RequestNumber;

                                Console.WriteLine("Sending the message back");

                                break;

                            }
                        case InventoryMessageType.Insert:
                            {
                                Console.WriteLine("Adding new pallet");
                                var pallet = JsonSerializer.Deserialize<Pallet>(inventoryMessage.Message);
                                palletService.Insert(pallet);
                                var PalletDataView = new PalletDataView
                                {
                                    PalletId = pallet.PalletId,
                                    Data = JsonSerializer.Serialize(pallet)
                                };
                                appDbContext.PalletDataView.Add(PalletDataView);
                                appDbContext.SaveChanges();

                                break;
                            }
                        case InventoryMessageType.Update:
                            {
                                Console.WriteLine("Updating a pallet");
                                var pallet = JsonSerializer.Deserialize<Pallet>(inventoryMessage.Message);
                                palletService.UpdateAsync(pallet);
                                var existingPallet = appDbContext.PalletDataView.Find(pallet.PalletId);
                                existingPallet.Data = JsonSerializer.Serialize(pallet);
                                appDbContext.SaveChanges();
                                break;
                            }
                        case InventoryMessageType.Delete:
                            {
                                Console.WriteLine("Deleting a pallet");
                                var id = JsonSerializer.Deserialize<int>(inventoryMessage.Message);
                                palletService.Remove(id);
                                var removePallet = appDbContext.PalletDataView.FirstOrDefault(predicate => predicate.PalletId == id);
                                appDbContext.PalletDataView.Remove(removePallet);
                                appDbContext.SaveChanges();
                                break;
                            }
                        case InventoryMessageType.Print:
                            {
                                Console.WriteLine("Printing");
                                var palletPrint = JsonSerializer.Deserialize<PalletPrint>(inventoryMessage.Message);
                                palletService.Print(palletPrint.Id, palletPrint.CustomerName);
                                break;
                            }
                        default: break;

                    }
                }

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine(ex.StackTrace);
            }

        }
    }
}
