import { Box, Card, CardActionArea, Chip, LinearProgress, Typography } from '@mui/material'
import PlayArrowIcon from '@mui/icons-material/PlayArrow'

type MediaCardProps = {
  title: string
  subtitle?: string
  badge?: string
  posterUrl?: string | null
  progress?: number
  onClick: (e: React.MouseEvent<HTMLElement>) => void
  onContextMenu?: (e: React.MouseEvent) => void
}

function hueFrom(text: string): number {
  let hash = 0
  for (let i = 0; i < text.length; i++) hash = (hash * 31 + text.charCodeAt(i)) % 360
  return hash
}

function initials(title: string): string {
  return title
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((w) => w[0]?.toUpperCase() ?? '')
    .join('')
}

export function MediaCard({ title, subtitle, badge, posterUrl, progress, onClick, onContextMenu }: MediaCardProps) {
  const hue = hueFrom(title)
  const bg = `linear-gradient(150deg, hsl(${hue} 45% 28%), hsl(${(hue + 40) % 360} 50% 14%))`

  return (
    <Card
      onContextMenu={onContextMenu}
      sx={{
        bgcolor: 'transparent',
        backgroundImage: 'none',
        boxShadow: 'none',
        transition: 'transform 0.18s ease',
        '&:hover': { transform: 'scale(1.04)' },
      }}
    >
      <CardActionArea onClick={onClick} sx={{ borderRadius: 1 }}>
        <Box
          sx={{
            position: 'relative',
            aspectRatio: '2/3',
            borderRadius: 1.5,
            background: bg,
            display: 'flex',
            alignItems: 'center',
            justifyContent: 'center',
            overflow: 'hidden',
          }}
        >
          {posterUrl ? (
            <Box
              component="img"
              src={posterUrl}
              alt={title}
              sx={{ width: '100%', height: '100%', objectFit: 'cover' }}
            />
          ) : (
            <Typography sx={{ fontSize: '2rem', fontWeight: 800, color: 'rgba(255,255,255,0.85)', letterSpacing: '0.05em' }}>
              {initials(title)}
            </Typography>
          )}

          {badge && (
            <Chip
              label={badge}
              size="small"
              sx={{
                position: 'absolute',
                top: 8,
                left: 8,
                bgcolor: 'rgba(0,0,0,0.55)',
                color: 'rgba(255,255,255,0.9)',
                fontSize: '0.65rem',
                height: 20,
              }}
            />
          )}

          <Box
            sx={{
              position: 'absolute',
              inset: 0,
              display: 'flex',
              alignItems: 'center',
              justifyContent: 'center',
              bgcolor: 'rgba(0,0,0,0.35)',
              opacity: 0,
              transition: 'opacity 0.18s ease',
              '&:hover': { opacity: 1 },
            }}
          >
            <PlayArrowIcon sx={{ fontSize: 48, color: '#fff' }} />
          </Box>

          {progress !== undefined && (
            <LinearProgress
              variant="determinate"
              value={progress}
              sx={{
                position: 'absolute',
                bottom: 0,
                left: 0,
                right: 0,
                height: 4,
                bgcolor: 'rgba(0,0,0,0.55)',
                '& .MuiLinearProgress-bar': { bgcolor: 'primary.main' },
              }}
            />
          )}
        </Box>

        <Typography
          variant="body2"
          sx={{
            mt: 0.8,
            lineHeight: 1.25,
            overflow: 'hidden',
            display: '-webkit-box',
            WebkitLineClamp: 2,
            WebkitBoxOrient: 'vertical',
          }}
        >
          {title}
        </Typography>
        {subtitle && (
          <Typography variant="caption" sx={{ color: 'text.secondary' }}>
            {subtitle}
          </Typography>
        )}
      </CardActionArea>
    </Card>
  )
}
