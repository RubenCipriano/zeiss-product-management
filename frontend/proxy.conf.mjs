// Where the dev server (`npm start`) sends /api: the API on this machine by default (`dotnet run` in the backend
// repository, or `kubectl -n product-management port-forward deployment/api 5080:8080`), or any environment's gateway,
// which serves the API under the same /api path:
//
//   npm start
//   API_URL=http://localhost:8080 npm start                       # the local cluster's gateway
//   API_URL=https://staging.products.example.com npm start        # staging
export default {
  '/api': {
    target: process.env.API_URL ?? 'http://localhost:5080',
    secure: false,
    changeOrigin: true,
  },
};
