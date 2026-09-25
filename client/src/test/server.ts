import { setupServer } from 'msw/node'

/**
 * A stand-in for the API at the network boundary.
 *
 * MSW intercepts `fetch` rather than replacing the module, so the code under test is the real
 * `api/client`: the same URL building, the same header handling, the same problem-details
 * parsing. Handlers are registered per test with `server.use` and reset between them.
 */
export const server = setupServer()
