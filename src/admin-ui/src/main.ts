import { createApp } from 'vue'
import { createPinia } from 'pinia'
import '@fontsource/carlito/latin-400.css'
import '@fontsource/carlito/latin-700.css'
import '@fontsource/carlito/latin-ext-400.css'
import '@fontsource/carlito/latin-ext-700.css'
import './styles/icons.css'
import './styles/base.css'
import App from './App.vue'
import { i18n } from './i18n'
import { createAppRouter } from './router'
import { configureClient } from './api/client'
import { useAuthStore } from './stores/auth'
import { useUiStore } from './stores/ui'

const app = createApp(App)
const pinia = createPinia()
app.use(pinia)
app.use(i18n)

const ui = useUiStore()
ui.init()

const auth = useAuthStore()
configureClient({
  onUnauthorized: () => auth.setUnauthenticated(),
})

const router = createAppRouter()
app.use(router)

void router.isReady().then(() => app.mount('#app'))
