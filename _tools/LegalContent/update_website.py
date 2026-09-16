"""One-time integration of the existing website's legal routes and footer."""
import json
import sys
from pathlib import Path

site = Path(sys.argv[1]).resolve()
assert (site / 'src/views/DisclaimerPage.vue').is_file()
template = Path(__file__).with_name('LegalPage.vue').read_text(encoding='utf-8')
main = site / 'src/main.ts'
text = main.read_text(encoding='utf-8')
if "@unhead/vue/client" not in text:
    text = text.replace("import { createApp } from 'vue'", "import { createApp } from 'vue'\nimport { createHead } from '@unhead/vue/client'")
    text = text.replace('app.use(router)', 'app.use(createHead())\napp.use(router)')
    main.write_text(text, encoding='utf-8')
(site / 'src/views/LegalPage.vue').write_text(template, encoding='utf-8')
(site / 'src/views/DisclaimerPage.vue').write_text('''<script setup lang="ts">
import LegalPage from './LegalPage.vue'
</script>

<template><LegalPage document-id="disclaimer" /></template>
''', encoding='utf-8')
router = site / 'src/router/index.ts'
text = router.read_text(encoding='utf-8')
if "name: 'terms'" not in text:
    text = text.replace("  routes: [", """  routes: [
    {
      path: '/terms',
      name: 'terms',
      component: () => import('@/views/LegalPage.vue'),
      props: { documentId: 'terms' },
    },
    {
      path: '/privacy',
      name: 'privacy',
      component: () => import('@/views/LegalPage.vue'),
      props: { documentId: 'privacy' },
    },""")
# Keep legal text out of the initial home-page bundle.
text = text.replace("import DisclaimerPage from '@/views/DisclaimerPage.vue'", "const DisclaimerPage = () => import('@/views/DisclaimerPage.vue')")
router.write_text(text, encoding='utf-8')
footer = site / 'src/components/layout/AppFooter.vue'
text = footer.read_text(encoding='utf-8').replace("href: 'https://github.com/Johnwikix/original-sound-hq-player/blob/main/policy.md'", "to: '/privacy'")
if "key: 'terms'" not in text:
    text = text.replace("const legalLinks = computed(() => [", "const legalLinks = computed(() => [\n  { key: 'terms', label: t('footer.legal.terms'), to: '/terms' },")
# All legal links are internal now; keep the other existing link lists unchanged.
text = text.replace('''<a v-if="link.href" :href="link.href" rel="nofollow">{{ link.label }}</a>
              <router-link v-else :to="link.to!">{{ link.label }}</router-link>
            </li>
          </ul>
        </div>

        <div class="footer-col">
          <h4 class="footer-title">{{ t('footer.col.social') }}''', '''<router-link :to="link.to">{{ link.label }}</router-link>
            </li>
          </ul>
        </div>

        <div class="footer-col">
          <h4 class="footer-title">{{ t('footer.col.social') }}''')
footer.write_text(text, encoding='utf-8')
for locale, title in [('zh', '用户协议'), ('en', 'User Agreement')]:
    path = site / 'src/locales' / f'{locale}.json'
    data = json.loads(path.read_text(encoding='utf-8'))
    data['footer']['legal']['terms'] = title
    data['footer']['legal']['policy'] = '隐私政策' if locale == 'zh' else 'Privacy Policy'
    data.pop('disclaimer', None)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print('Updated legal page, routes, footer and locales.')
