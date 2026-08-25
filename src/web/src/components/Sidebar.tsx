import { useState } from 'react'
import { NavLink } from 'react-router-dom'
import {
  Backdrop,
  Box,
  Drawer,
  List,
  ListItemButton,
  ListItemIcon,
  ListItemText,
  Typography,
} from '@mui/material'
import HomeIcon from '@mui/icons-material/Home'
import TvIcon from '@mui/icons-material/Tv'
import MovieIcon from '@mui/icons-material/Movie'
import RateReviewIcon from '@mui/icons-material/RateReview'
import SettingsIcon from '@mui/icons-material/Settings'

const COLLAPSED = 60
const EXPANDED = 210

const navItems: Array<{ to: string; icon: React.ReactNode; label: string; end?: boolean }> = [
  { to: '/', icon: <HomeIcon />, label: 'Home', end: true },
  { to: '/shows', icon: <TvIcon />, label: 'Seriály' },
  { to: '/movies', icon: <MovieIcon />, label: 'Filmy' },
  { to: '/review', icon: <RateReviewIcon />, label: 'Ke kontrole' },
]

export function Sidebar() {
  const [expanded, setExpanded] = useState(false)
  const width = expanded ? EXPANDED : COLLAPSED

  return (
    <>
      <Backdrop
        open={expanded}
        onClick={() => setExpanded(false)}
        sx={{ zIndex: (t) => t.zIndex.drawer + 1 }}
      />
      <Drawer
        variant="permanent"
        onMouseEnter={() => setExpanded(true)}
        onMouseLeave={() => setExpanded(false)}
        sx={{
          width: COLLAPSED,
          flexShrink: 0,
          '& .MuiDrawer-paper': {
            width,
            transition: 'width 0.22s ease',
            overflowX: 'hidden',
            display: 'flex',
            flexDirection: 'column',
            borderRight: '1px solid rgba(255,255,255,0.07)',
            position: 'fixed',
            top: 0,
            left: 0,
            height: '100vh',
            zIndex: (t) => t.zIndex.drawer + 2,
          },
        }}
      >
      <Box sx={{ py: 2, textAlign: 'center' }}>
        <Typography
          sx={{ fontWeight: 800, letterSpacing: '0.12em', color: 'primary.main', fontSize: '1.2rem' }}
        >
          LSP
        </Typography>
      </Box>

      <List sx={{ flex: 1, pt: 0.5 }}>
        {navItems.map(({ to, icon, label, end }) => (
          <ListItemButton
            key={to}
            component={NavLink}
            to={to}
            end={end}
            sx={{
              minHeight: 44,
              px: 2,
              '&.active': {
                bgcolor: 'rgba(229,9,20,0.15)',
                borderRight: '3px solid',
                borderColor: 'primary.main',
                '& .MuiListItemIcon-root': { color: 'primary.main' },
                '& .MuiListItemText-primary': { color: '#fff' },
              },
            }}
          >
            <ListItemIcon sx={{ minWidth: 36, color: 'rgba(255,255,255,0.55)' }}>{icon}</ListItemIcon>
            <ListItemText
              primary={label}
              sx={{
                opacity: expanded ? 1 : 0,
                transition: 'opacity 0.18s ease',
                whiteSpace: 'nowrap',
              }}
            />
          </ListItemButton>
        ))}
      </List>

      <List>
        <ListItemButton
          component={NavLink}
          to="/settings"
          sx={{
            minHeight: 44,
            px: 2,
            '&.active': {
              bgcolor: 'rgba(229,9,20,0.15)',
              borderRight: '3px solid',
              borderColor: 'primary.main',
              '& .MuiListItemIcon-root': { color: 'primary.main' },
            },
          }}
        >
          <ListItemIcon sx={{ minWidth: 36, color: 'rgba(255,255,255,0.55)' }}>
            <SettingsIcon />
          </ListItemIcon>
          <ListItemText
            primary="Nastavení"
            sx={{ opacity: expanded ? 1 : 0, transition: 'opacity 0.18s ease', whiteSpace: 'nowrap' }}
          />
        </ListItemButton>
      </List>
      </Drawer>
    </>
  )
}
