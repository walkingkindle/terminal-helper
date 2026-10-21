import { readSse, SseEvent } from './sse';

function streamOf(chunks: (string | Uint8Array)[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(typeof chunk === 'string' ? encoder.encode(chunk) : chunk);
      controller.close();
    },
  });
}

async function collect(stream: ReadableStream<Uint8Array>) {
  const events: SseEvent[] = [];
  for await (const event of readSse(stream)) events.push(event);
  return events;
}

describe('readSse', () => {
  it('reads named events', async () => {
    const events = await collect(streamOf(['event: delta\ndata: {"text":"a"}\n\nevent: done\ndata: {}\n\n']));
    expect(events).toEqual([
      { event: 'delta', data: '{"text":"a"}' },
      { event: 'done', data: '{}' },
    ]);
  });

  it('copes with chunks split anywhere, CRLF and comments', async () => {
    const events = await collect(
      streamOf([': keep-alive\r\n\r\nev', 'ent: delta\r', '\ndata: {"text":', '"b"}\r\n', '\r\ndata: tail']),
    );
    expect(events).toEqual([
      { event: 'delta', data: '{"text":"b"}' },
      { event: 'message', data: 'tail' },
    ]);
  });

  it('keeps multi-byte characters split across chunks', async () => {
    const bytes = new TextEncoder().encode('data: héllo ✓\n\n');
    const events = await collect(streamOf([bytes.slice(0, 8), bytes.slice(8, 15), bytes.slice(15)]));
    expect(events).toEqual([{ event: 'message', data: 'héllo ✓' }]);
  });
});
