import { createApp } from 'vue'
import { createPinia } from 'pinia'
import './styles/main.css'
import App from './App.vue'
import { createAppRouter } from './router'
import { configureClient } from './api/client'
import { useAuthStore } from './stores/auth'
import { useUiStore } from './stores/ui'

const app = createApp(App)
app.use(createPinia())

useUiStore().init()

const auth = useAuthStore()
const ui = useUiStore()
configureClient({
  onUnauthorized: () => auth.setUnauthenticated(),
  onChangeDelayed: () => ui.notify('Saved. The gateways apply it when their cache refreshes (30 seconds by default).'),
})

const router = createAppRouter()
app.use(router)

void router.isReady().then(() => app.mount('#app'))
