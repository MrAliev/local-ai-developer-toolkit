import { memo, forwardRef } from 'react';

export const Plain = ({ label }: { label: string }) => {
  return <span>{label}</span>;
};

export const Memoized = memo(({ label }: { label: string }) => {
  return <span>{label}</span>;
});

export const Forwarded = forwardRef((props: { label: string }) => {
  return <span>{props.label}</span>;
});

export const Named = memo(function NamedInner({ label }: { label: string }) {
  return <span>{label}</span>;
});
