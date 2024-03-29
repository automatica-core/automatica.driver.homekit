﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using P3.Driver.HomeKit.Bonjour.Abstraction;
using Microsoft.Extensions.Logging;

namespace P3.Driver.HomeKit.Bonjour
{
    internal static class ListTxtHelper
    {
        internal static List<string> AddProperty(this List<string> list, string key, string value)
        {
            list.Add($"{key}={value}");
            return list;
        }
    }

    public class BonjourService
    {
        private readonly ILogger _logger;
        private readonly ushort _port;
        private readonly string _name;
        private readonly string _hapId;
        private readonly int _configVersion;
        private readonly MulticastService _mdns;

        internal const string HapName = "_hap";
        internal static readonly string DnsHapDomain = $"{HapName}._tcp.local";

        public bool AlreadyPaired { get; set; }

        public BonjourService(ILogger logger, ushort port, string name, string hapId, int configVersion)
        {
            _logger = logger;
            _port = port;
            _name = name;
            _hapId = hapId;
            _configVersion = configVersion;
            _mdns = new MulticastService();
        }

        private Message GenerateQueryResponseMessage()
        {
            var message = new Message {QR = true};

            var txtList = new List<string>();
            txtList.AddProperty("sf", "1");
            txtList.AddProperty("c#", _configVersion.ToString());
            txtList.AddProperty("s#", "1");
            txtList.AddProperty("md", _name);
            txtList.AddProperty("ff", "0");
            txtList.AddProperty("id", _hapId);
            txtList.AddProperty("ci", "2");
            txtList.AddProperty("pv", "1.1");

            message.Answers.Add(new TXTRecord
            {
                Name = $"{_name}.{DnsHapDomain}", 
                Class = DnsClass.IN, 
                Type = DnsType.TXT,
                Strings = txtList, 
                TTL = TimeSpan.FromMinutes(75)
            });
            message.Answers.Add(new PTRRecord
            {
                Name = ServiceDiscovery.ServiceName, 
                DomainName = DnsHapDomain, 
                Class = DnsClass.IN, 
                Type = DnsType.PTR,
                TTL = TimeSpan.FromMinutes(75)
            });
            message.Answers.Add(new PTRRecord
            {
                Name = DnsHapDomain, 
                DomainName = $"{_name}.{DnsHapDomain}", 
                Class = DnsClass.IN, 
                Type = DnsType.PTR,
                TTL = TimeSpan.FromMinutes(75)
            });
            message.Answers.Add(new SRVRecord
            {
                Name = $"{_name}.{DnsHapDomain}", 
                Port = _port, 
                Target = $"{_hapId.Replace(":", "_")}.local", 
                Weight = 0, 
                Class = DnsClass.IN, 
                Type = DnsType.SRV,
                TTL = TimeSpan.FromMinutes(2)
            });

            return message;
        }

        public Task Start()
        {
            _mdns.QueryReceived += _mdns_QueryReceived;

            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces();

            foreach (var network in networkInterfaces)
            {
                _logger.LogDebug(
                    $"Interface {network.Name}, Type {network.NetworkInterfaceType}, OP Status {network.OperationalStatus}, IP {network.GetIPProperties().UnicastAddresses.FirstOrDefault()}");
            }

            var weUseAddresses = MulticastService.GetIpAddresses();

            foreach (var ip in weUseAddresses)
            {
                _logger.LogDebug($"We use {ip}");
            }

            var startMessage = new Message();
            startMessage.AuthorityRecords.Add(new SRVRecord
            {
                Name = $"{_name}.{DnsHapDomain}",
                Port = _port,
                Target = $"{_hapId.Replace(":", "_")}.local"
            });

            var q = new Question
            {
                Type = DnsType.ANY,
                Name = $"{_name}.{DnsHapDomain}",
                QU = true,
                Class = DnsClass.IN
            };
            startMessage.Questions.Add(q);

            _mdns.Start();

#pragma warning disable 4014
            Task.Run(async () =>
            {
                _mdns.SendQuery(startMessage);
                await Task.Delay(1000);
                q.QU = false;
                _mdns.SendQuery(startMessage);
                await Task.Delay(1000);
                _mdns.SendQuery(startMessage);
                await Task.Delay(1000);
            }).ConfigureAwait(false);
#pragma warning restore 4014
            return Task.CompletedTask;
        }

        public async Task Stop()
        {
            var message = new Message();
            message.Answers.Add(new PTRRecord
            {
                Name = DnsHapDomain,
                DomainName = $"{_name}.{DnsHapDomain}",
                Type = DnsType.PTR,
                TTL = TimeSpan.FromSeconds(0),
                Class = DnsClass.IN
            });

            _mdns.Send(message, false);
            await Task.Delay(2000);
            _mdns.Send(message, false);
            await Task.Delay(2000);
            _mdns.Send(message, false);

            _mdns.Stop();
        }

        private void _mdns_QueryReceived(object sender, MessageEventArgs e)
        {
            if (e.Message.IsQuery && 
            (e.Message.Questions.Any(a => a.Name.Labels.Contains(HapName))) || 
            e.Message.Questions.Any(a => a.Name == ServiceDiscovery.ServiceName))
            {
                var localAddresses = MulticastService.GetIpAddresses();
                var dnsMessage = GenerateQueryResponseMessage();

                foreach (var localAddress in localAddresses)
                {
                    if (localAddress.AddressFamily == AddressFamily.InterNetwork)
                    {
                        dnsMessage.Answers.Add(new ARecord
                        {
                            Name = $"{_hapId.Replace(":", "_")}.local",
                            Address = localAddress,
                            Class = DnsClass.IN,
                            Type = DnsType.A,
                            TTL = TimeSpan.FromMinutes(2)
                        });
                    }
                    else if (localAddress.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        dnsMessage.Answers.Add(new AAAARecord
                        {
                            Name = $"{_hapId.Replace(":", "_")}.local",
                            Address = localAddress,
                            Class = DnsClass.IN,
                            Type = DnsType.AAAA,
                            TTL = TimeSpan.FromMinutes(2)
                        });
                    }
                }

                _mdns.SendAnswer(dnsMessage, e);
            }
        }


    }
}
