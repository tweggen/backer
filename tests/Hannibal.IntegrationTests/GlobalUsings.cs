global using Xunit;

/*
 * The API harness (PostgresFixture, BackerApiFactory, the recording hub and the
 * OAuth2 transport stub) moved to the TestSupport.Api library so the full-loop
 * suite can host the API the same way. Imported globally to keep the move out
 * of every test file.
 */
global using TestSupport.Api;
