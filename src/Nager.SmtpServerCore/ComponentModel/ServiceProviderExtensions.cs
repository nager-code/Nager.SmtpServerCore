using System;

namespace Nager.SmtpServerCore.ComponentModel
{
    internal static class ServiceProviderExtensions
    {
        internal static TService GetServiceOrDefault<TService>(this IServiceProvider serviceProvider, TService @default) where TService : class
        {
            if (serviceProvider == null)
            {
                throw new ArgumentNullException(nameof(serviceProvider));
            }

            return serviceProvider.GetService(typeof(TService)) as TService ?? @default;
        }

        /// <summary>
        /// Returns true if either a session factory or a plain instance of the service has been registered.
        /// </summary>
        /// <typeparam name="TServiceFactory">The factory type that creates session scoped instances of the service.</typeparam>
        /// <typeparam name="TService">The service type.</typeparam>
        /// <param name="serviceProvider">The service provider to perform the lookup on.</param>
        /// <returns>true if the service can be resolved, false if not.</returns>
        internal static bool HasService<TServiceFactory, TService>(this IServiceProvider serviceProvider)
            where TService : class
            where TServiceFactory : ISessionContextInstanceFactory<TService>
        {
            if (serviceProvider == null)
            {
                throw new ArgumentNullException(nameof(serviceProvider));
            }

            return serviceProvider.GetService(typeof(TServiceFactory)) is TServiceFactory
                || serviceProvider.GetService(typeof(TService)) is TService;
        }

        internal static TService GetService<TServiceFactory, TService>(this IServiceProvider serviceProvider, ISessionContext sessionContext, TService @default)
            where TService : class
            where TServiceFactory : ISessionContextInstanceFactory<TService>
        {
            if (serviceProvider == null)
            {
                throw new ArgumentNullException(nameof(serviceProvider));
            }

            if (serviceProvider.GetService(typeof(TServiceFactory)) is TServiceFactory sessionFactory)
            {
                return sessionFactory.CreateInstance(sessionContext);
            }

            return serviceProvider.GetServiceOrDefault(@default);
        }
    }
}
