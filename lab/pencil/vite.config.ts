import { defineConfig } from 'vite'
import tailwindcss from '@tailwindcss/vite'
import vue from '@vitejs/plugin-vue'
import IconsResolver from 'unplugin-icons/resolver'
import Icons from 'unplugin-icons/vite'
import Components from 'unplugin-vue-components/vite'
import { resolve } from 'node:path'
import { ensureBrandAssets } from '@open-pencil/brand-tools'
import { createOpenPencilAliases } from './vite/aliases'
import { copyCanvasKitAssetsPlugin } from './vite/canvaskit-assets'
import { rawMarkdownPlugin } from './vite/raw-markdown'

export default defineConfig(async () => {
  await ensureBrandAssets(['web'])
  return {
    base: '/pencil/',
    resolve: { alias: [{find: '@open-pencil/mcp/tools', replacement: resolve(__dirname, 'packages/mcp/src/tool/index.ts')}, ...createOpenPencilAliases(__dirname)] },
    define: {
      __OPENPENCIL_APP_VERSION__: JSON.stringify('0.15.1 · 7 Days Mod Lab'),
      __OPENPENCIL_LOCAL_AUTOMATION_TOKEN__: JSON.stringify(''),
      __OPENPENCIL_LOCAL_AUTOMATION_URL__: JSON.stringify(''),
      __OPENPENCIL_LOCAL_AUTOMATION_HTTP_URL__: JSON.stringify('')
    },
    plugins: [rawMarkdownPlugin(), copyCanvasKitAssetsPlugin(), tailwindcss(),
      Icons({ compiler: 'vue3' }), Components({ resolvers: [IconsResolver({ prefix: 'icon' })] }), vue()],
    build: { chunkSizeWarningLimit: 2500 }
  }
})
