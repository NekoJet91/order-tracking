using OrderTracking.IntegrationTests;
using Xunit.v3;

// One PostgreSQL server and one RabbitMQ broker for the whole run, started before the first
// test and disposed after the last. Classes run in parallel: each fixture takes its own
// database from that server and its own queue names on that broker, so nothing is shared
// that two of them could collide over.
[assembly: AssemblyFixture(typeof(ContainerEnvironment))]
