<!-- Project Insight MCP Server -->
# Project Insight MCP Server

This project creates a Model Context Protocol (MCP) server for the Project Insight API, enabling AI agents to interact with project management data and tools.

## Key Features

- **Project Management Tools**: Search, list, create, update projects
- **Task Management**: Handle project tasks and assignments  
- **Resource Management**: Manage project resources and allocations
- **Authentication**: Token-based authentication with Project Insight API
- **Type Safety**: Full TypeScript implementation with proper types

## API Integration

- Base URL pattern: `https://yourdomain.projectinsight.net/api/`
- Authentication: API token-based
- Custom property selection via `modelProperties` parameter
- Extensive project and task data models

## Implementation Guidelines

- Follow MCP TypeScript SDK best practices
- Implement proper error handling for API failures
- Use structured logging (stderr only for STDIO transport)
- Validate all inputs with proper schemas
- Handle Project Insight's custom property selection system
- Implement rate limiting awareness

## Security Considerations

- Store API credentials securely (environment variables)
- Validate API responses
- Handle sensitive project data appropriately
- Implement proper CORS for HTTP transport

## Testing Strategy

- Use MCP Inspector for development testing
- Test with Claude Desktop or other MCP clients
- Verify authentication and API connectivity
- Test error scenarios and edge cases