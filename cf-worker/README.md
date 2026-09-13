# DWGC2E Cloudflare Worker

## Deploy

```powershell
cd D:\DWGC2E\cf-worker
npm install
npx wrangler login
npx wrangler d1 execute dwg-translator-prod --remote --file=./schema.sql
npx wrangler secret put DEEPSEEK_API_KEY
npx wrangler secret put JWT_SECRET
npx wrangler secret put PASSWORD_PEPPER
npx wrangler secret put ADMIN_API_KEY
npx wrangler deploy
```

The D1 binding is `DB`. Never commit secret values.
