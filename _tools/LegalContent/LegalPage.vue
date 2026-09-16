<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useHead } from '@unhead/vue'
import PageLayout from '../components/layout/PageLayout.vue'
import zh from '../data/legal/legal.zh-CN.json'
import en from '../data/legal/legal.en.json'

const props = defineProps<{ documentId: 'terms' | 'privacy' | 'disclaimer' }>()
const { locale } = useI18n()
const isChinese = computed(() => locale.value.startsWith('zh'))
const bundle = computed(() => (isChinese.value ? zh : en))
const document = computed(() => bundle.value.documents.find((item) => item.id === props.documentId)!)
const versionLabel = computed(() => (isChinese.value ? '版本' : 'Version'))
const effectiveLabel = computed(() => (isChinese.value ? '生效日期' : 'Effective date'))
const navigationLabel = computed(() => (isChinese.value ? '法律文件' : 'Legal documents'))
const contentsLabel = computed(() => (isChinese.value ? '本页内容' : 'On this page'))
const importantLabel = computed(() => (isChinese.value ? '重要条款' : 'Important terms'))
useHead(() => ({
  title: `${document.value.title} · OriginalSound HQ`,
  meta: [{ name: 'description', content: document.value.summary }],
}))
</script>

<template>
  <PageLayout>
    <div class="legal-page">
      <header class="legal-header">
        <nav class="legal-navigation" :aria-label="navigationLabel">
          <RouterLink
            v-for="item in bundle.documents"
            :key="item.id"
            :to="`/${item.id}`"
            :aria-current="item.id === documentId ? 'page' : undefined"
          >{{ item.title }}</RouterLink>
        </nav>
        <h1>{{ document.title }}</h1>
        <p class="legal-version">{{ versionLabel }} {{ bundle.version }} · {{ effectiveLabel }} {{ bundle.version.split('.')[0] }}</p>
        <p class="legal-summary">{{ document.summary }}</p>
      </header>

      <div class="legal-layout">
        <nav class="legal-contents" :aria-label="contentsLabel">
          <p>{{ contentsLabel }}</p>
          <a v-for="(section, index) in document.sections" :key="section.title" :href="`#section-${index}`">
            {{ section.title }}
          </a>
        </nav>
        <article :aria-label="document.title" class="legal-document">
          <section
            v-for="(section, index) in document.sections"
            :id="`section-${index}`"
            :key="`${documentId}-${index}`"
            class="legal-section"
          >
            <p v-if="'important' in section && section.important" class="legal-important">{{ importantLabel }}</p>
            <h2>{{ section.title }}</h2>
            <p v-for="paragraph in section.paragraphs" :key="paragraph">{{ paragraph }}</p>
          </section>
          <p class="legal-contact">
            {{ isChinese ? '联系开发者：' : 'Contact: ' }}
            <a href="mailto:dannypan9709@foxmail.com">dannypan9709@foxmail.com</a>
          </p>
        </article>
      </div>
    </div>
  </PageLayout>
</template>

<style scoped>
.legal-page {
  max-width: 1160px;
  margin: 0 auto;
  padding: calc(var(--header-height) + var(--space-8)) var(--space-6) var(--space-8);
  color: var(--text-primary);
}
.legal-header { max-width: 75ch; margin-bottom: var(--space-8); }
.legal-navigation { display: flex; flex-wrap: wrap; gap: 8px 24px; margin-bottom: 32px; }
.legal-navigation a { display: inline-flex; align-items: center; min-height: 44px; color: var(--text-secondary); }
.legal-navigation a[aria-current='page'] { color: var(--text-primary); text-decoration: underline; text-underline-offset: 8px; }
h1 { font-size: clamp(30px, 4vw, 48px); letter-spacing: -0.02em; text-wrap: balance; margin: 0 0 16px; }
.legal-version { font-size: 14px; color: var(--text-secondary); margin-bottom: 24px; }
.legal-summary { font-size: 17px; line-height: 1.85; }
.legal-layout { display: grid; grid-template-columns: minmax(180px, 240px) minmax(0, 1fr); gap: 48px; }
.legal-contents { display: flex; flex-direction: column; align-self: start; position: sticky; top: calc(var(--header-height) + 24px); }
.legal-contents p { margin: 0 0 12px; font-weight: 600; }
.legal-contents a { display: flex; align-items: center; min-height: 44px; padding: 8px 0; line-height: 1.5; color: var(--text-secondary); }
.legal-document { max-width: 75ch; min-width: 0; overflow-wrap: anywhere; }
.legal-section { padding: 0 0 28px; margin-bottom: 28px; border-bottom: 1px solid var(--border-subtle); scroll-margin-top: calc(var(--header-height) + 24px); }
h2 { font-size: clamp(20px, 2.2vw, 25px); line-height: 1.5; margin: 0 0 16px; text-wrap: balance; }
.legal-section p { font-size: 16px; line-height: 1.9; margin: 0 0 16px; }
.legal-section .legal-important { font-size: 14px; font-weight: 700; margin-bottom: 8px; }
.legal-contact { line-height: 1.8; }
.legal-contact a { text-decoration: underline; text-underline-offset: 4px; }
a:focus-visible { outline: 2px solid var(--accent); outline-offset: 4px; border-radius: 2px; }
@media (max-width: 760px) {
  .legal-page { padding-inline: 20px; }
  .legal-layout { grid-template-columns: minmax(0, 1fr); gap: 32px; }
  .legal-contents { position: static; padding-bottom: 24px; border-bottom: 1px solid var(--border-subtle); }
}
@media print {
  .legal-page { color: #000; padding: 0; max-width: none; }
  .legal-navigation, .legal-contents { display: none; }
  .legal-layout { display: block; }
  .legal-section { break-inside: avoid; }
  .legal-version { color: #333; }
}
</style>
