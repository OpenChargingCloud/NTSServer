/*
 * The stand-in node every kind's page tests share - WWCP_Node's test/node.ts -
 * as this NTS server: a stand-in for fetch, and a document of happy-dom to
 * draw a page into. An NTS server adds nothing of its own to it.
 *
 * Imported first, before a page: lit-html looks for the document as it is
 * loaded, and the pages load it.
 *
 *   import { open, ... } from '../../test/ntsServer.ts';
 *   const { timeServerPage } = await import('./timeServer.ts');
 */

import { standIn } from '@node/../test/node.ts';

export * from '@node/../test/node.ts';

standIn({ name: 'NTS Server', icon: 'fa-clock' });
