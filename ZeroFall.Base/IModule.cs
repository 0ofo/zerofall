using System;
using Microsoft.Extensions.DependencyInjection;

namespace ZeroFall.Base;

public interface IModule
{
    void RegisterServices(IServiceCollection services);
    void Initialize(IServiceProvider serviceProvider);
}
