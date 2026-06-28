using System;
using Microsoft.Extensions.DependencyInjection;

namespace Datafinder.Base;

public interface IModule
{
    void RegisterServices(IServiceCollection services);
    void Initialize(IServiceProvider serviceProvider);
}
