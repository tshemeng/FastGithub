﻿using DNS.Client;
using DNS.Protocol;
using FastGithub.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace FastGithub.DomainResolve
{
    /// <summary>
    /// 域名解析器
    /// </summary> 
    sealed class DomainResolver : IDomainResolver
    {
        private readonly IMemoryCache blackIPAddressCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
        private readonly IMemoryCache domainResolveCache = new MemoryCache(Options.Create(new MemoryCacheOptions()));

        private readonly FastGithubConfig fastGithubConfig;
        private readonly DnscryptProxy dnscryptProxy;
        private readonly ILogger<DomainResolver> logger;

        private readonly TimeSpan lookupTimeout = TimeSpan.FromSeconds(5d);
        private readonly TimeSpan connectTimeout = TimeSpan.FromSeconds(5d);
        private readonly TimeSpan dnscryptExpiration = TimeSpan.FromMinutes(5d);
        private readonly TimeSpan fallbackExpiration = TimeSpan.FromMinutes(1d);
        private readonly TimeSpan loopbackExpiration = TimeSpan.FromSeconds(5d);
        private readonly ConcurrentDictionary<DnsEndPoint, SemaphoreSlim> semaphoreSlims = new();

        /// <summary>
        /// 域名解析器
        /// </summary> 
        /// <param name="fastGithubConfig"></param>
        /// <param name="dnscryptProxy"></param>
        /// <param name="logger"></param>
        public DomainResolver(
            FastGithubConfig fastGithubConfig,
            DnscryptProxy dnscryptProxy,
            ILogger<DomainResolver> logger)
        {
            this.fastGithubConfig = fastGithubConfig;
            this.dnscryptProxy = dnscryptProxy;
            this.logger = logger;
        }

        /// <summary>
        /// 设置ip黑名单
        /// </summary>
        /// <param name="address">ip</param>
        /// <param name="expiration">过期时间</param>
        public void SetBlack(IPAddress address, TimeSpan expiration)
        {
            this.blackIPAddressCache.Set(address, address, expiration);
        }

        /// <summary>
        /// 刷新域名解析结果
        /// </summary>
        /// <param name="domain">域名</param>
        public void FlushDomain(DnsEndPoint domain)
        {
            this.domainResolveCache.Remove(domain);
        }

        /// <summary>
        /// 解析域名
        /// </summary>
        /// <param name="domain"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<IPAddress> ResolveAsync(DnsEndPoint domain, CancellationToken cancellationToken = default)
        {
            var semaphore = this.semaphoreSlims.GetOrAdd(domain, _ => new SemaphoreSlim(1, 1));
            try
            {
                await semaphore.WaitAsync();
                return await this.LookupAsync(domain, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }

        /// <summary>
        /// 查找ip
        /// </summary>
        /// <param name="domain"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<IPAddress> LookupAsync(DnsEndPoint domain, CancellationToken cancellationToken)
        {
            if (this.domainResolveCache.TryGetValue<IPAddress>(domain, out var address))
            {
                return address;
            }

            var expiration = this.dnscryptExpiration;
            if (this.dnscryptProxy.LocalEndPoint != null)
            {
                address = await this.LookupCoreAsync(this.dnscryptProxy.LocalEndPoint, domain, cancellationToken);
            }

            if (address == null)
            {
                expiration = this.fallbackExpiration;
                address = await this.FallbackLookupAsync(domain, cancellationToken);
            }

            if (address == null)
            {
                throw new FastGithubException($"当前解析不到{domain.Host}可用的ip，请刷新重试");
            }

            // 往往是被污染的dns
            if (address.Equals(IPAddress.Loopback) == true)
            {
                expiration = this.loopbackExpiration;
            }

            this.logger.LogInformation($"[{domain.Host}->{address}]");
            this.domainResolveCache.Set(domain, address, expiration);
            return address;
        }

        /// <summary>
        /// 回退查找ip
        /// </summary>
        /// <param name="domain"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<IPAddress?> FallbackLookupAsync(DnsEndPoint domain, CancellationToken cancellationToken)
        {
            foreach (var dns in this.fastGithubConfig.FallbackDns)
            {
                var address = await this.LookupCoreAsync(dns, domain, cancellationToken);
                if (address != null)
                {
                    return address;
                }
            }
            return default;
        }


        /// <summary>
        /// 查找ip
        /// </summary>
        /// <param name="dns"></param>
        /// <param name="domain"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<IPAddress?> LookupCoreAsync(IPEndPoint dns, DnsEndPoint domain, CancellationToken cancellationToken)
        {
            try
            {
                var dnsClient = new DnsClient(dns);
                using var timeoutTokenSource = new CancellationTokenSource(this.lookupTimeout);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token);
                var addresses = await dnsClient.Lookup(domain.Host, RecordType.A, linkedTokenSource.Token);
                return await this.FindFastValueAsync(addresses, domain.Port, cancellationToken);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"dns({dns})无法解析{domain.Host}：{ex.Message}");
                return default;
            }
        }


        /// <summary>
        /// 获取最快的ip
        /// </summary>
        /// <param name="addresses"></param>
        /// <param name="port"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<IPAddress?> FindFastValueAsync(IEnumerable<IPAddress> addresses, int port, CancellationToken cancellationToken)
        {
            if (addresses.Any() == false)
            {
                return default;
            }

            var tasks = addresses.Select(address => this.IsAvailableAsync(address, port, cancellationToken));
            var fastTask = await Task.WhenAny(tasks);
            return await fastTask;
        }


        /// <summary>
        /// 验证远程节点是否可连接
        /// </summary>
        /// <param name="address"></param>
        /// <param name="port"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task<IPAddress?> IsAvailableAsync(IPAddress address, int port, CancellationToken cancellationToken)
        {
            if (port <= 0)
            {
                return address;
            }

            if (this.blackIPAddressCache.TryGetValue(address, out _))
            {
                return default;
            }

            try
            {
                using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                using var timeoutTokenSource = new CancellationTokenSource(this.connectTimeout);
                using var linkedTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutTokenSource.Token);
                await socket.ConnectAsync(address, port, linkedTokenSource.Token);
                return address;
            }
            catch (OperationCanceledException)
            {
                this.SetBlack(address, TimeSpan.FromSeconds(10d));
                return default;
            }
            catch (Exception)
            {
                this.SetBlack(address, TimeSpan.FromSeconds(10d));
                await Task.Delay(this.connectTimeout, cancellationToken);
                return default;
            }
        }
    }
}
